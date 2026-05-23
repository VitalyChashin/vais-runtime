// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Extensions.Container;

/// <summary>
/// Manages the lifecycle of <c>host: container</c> extensions: starts the container,
/// discovers handlers via <c>GET /v1/handlers</c>, cross-checks against the manifest,
/// and inserts <see cref="HandlerBinding"/>s into the <see cref="ExtensionHandlerRegistry"/>.
/// Parallel to <c>ContainerPluginLifecycleManager</c> in <c>Vais.Agents.Runtime.Plugins.Container</c>.
/// </summary>
internal sealed class ContainerExtensionLifecycleManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Maps a seam name to the C# middleware adapter that fronts its HTTP proxy. Add a seam = add a row.</summary>
    private static readonly IReadOnlyDictionary<string, Func<HttpContainerHandlerProxy, object>> SeamAdapters =
        new Dictionary<string, Func<HttpContainerHandlerProxy, object>>(StringComparer.Ordinal)
        {
            [ExtensionSeams.AgentInput]            = proxy => new AgentInputHandlerProxy(proxy),
            [ExtensionSeams.AgentOutput]          = proxy => new AgentOutputHandlerProxy(proxy),
            [ExtensionSeams.ToolGatewayMiddleware] = proxy => new ToolGatewayHandlerProxy(proxy),
            [ExtensionSeams.LlmGatewayMiddleware] = proxy => new LlmGatewayHandlerProxy(proxy),
        };

    private readonly ExtensionHandlerRegistry _registry;
    private readonly IExtensionChainComposer _composer;
    private readonly IContainerExtensionHost _containerHost;
    private readonly ILogger<ContainerExtensionLifecycleManager> _logger;

    /// <summary>DI ctor.</summary>
    public ContainerExtensionLifecycleManager(
        ExtensionHandlerRegistry registry,
        IExtensionChainComposer composer,
        IContainerExtensionHost? containerHost = null,
        ILogger<ContainerExtensionLifecycleManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(composer);
        _registry = registry;
        _composer = composer;
        _containerHost = containerHost ?? NullContainerExtensionHost.Instance;
        _logger = logger ?? NullLogger<ContainerExtensionLifecycleManager>.Instance;
    }

    /// <summary>
    /// Apply a <c>host: container</c> extension manifest: start the container, discover handlers,
    /// and register them in the <see cref="ExtensionHandlerRegistry"/>.
    /// </summary>
    public async Task<ExtensionReloadResult> ApplyAsync(
        ExtensionManifest manifest,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        _logger.LogInformation("container-ext-apply-begin: {Id} v{Version}", manifest.Id, manifest.Version);

        _registry.Snapshot().TryGetValue(manifest.Id, out var old);

        // 1. Start container (or no-op if operator-managed).
        Uri? baseUri;
        try
        {
            baseUri = await _containerHost.StartAsync(manifest, ct).ConfigureAwait(false);
            baseUri ??= FormBaseUri(manifest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "container-ext-start-failed: {Id}", manifest.Id);
            return Failure(old, ExtensionUrns.ExtensionReloadFailed, ex);
        }

        if (baseUri is null)
        {
            _logger.LogWarning("container-ext-no-url: {Id} — spec.image or spec.port missing", manifest.Id);
            return Failure(old, ExtensionUrns.HandlerDiscoveryFailed, null);
        }

        // 2. Discover advertised handlers via GET /v1/handlers.
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        ContainerHandlerAdvertisement? advertised;
        try
        {
            advertised = await http.GetFromJsonAsync<ContainerHandlerAdvertisement>(
                "/v1/handlers", JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "container-ext-discovery-failed: GET /v1/handlers on {BaseUri}", baseUri);
            return Failure(old, ExtensionUrns.HandlerDiscoveryFailed, ex);
        }

        if (advertised is null)
        {
            _logger.LogWarning("container-ext-discovery-null: {Id} returned empty body", manifest.Id);
            return Failure(old, ExtensionUrns.HandlerDiscoveryFailed, null);
        }

        // 3. Cross-check manifest handler ids against advertised.
        var manifestIds  = manifest.Spec.Handlers.Select(h => h.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var advertisedIds = advertised.Handlers.Select(h => h.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!manifestIds.SetEquals(advertisedIds))
        {
            _logger.LogWarning(
                "container-ext-mismatch: {Id} manifest={M} advertised={A}",
                manifest.Id,
                string.Join(",", manifestIds),
                string.Join(",", advertisedIds));
            return Failure(old, ExtensionUrns.HandlerMismatch, null);
        }

        // 4. Build HandlerBindings — one proxy per handler.
        var bindings = new List<HandlerBinding>(manifest.Spec.Handlers.Count);
        foreach (var handler in manifest.Spec.Handlers)
        {
            var adv = advertised.Handlers.FirstOrDefault(h =>
                string.Equals(h.Id, handler.Id, StringComparison.OrdinalIgnoreCase));
            if (adv is null) continue;

            var preEndpoint  = adv.PreEndpoint  ?? $"/handlers/{handler.Id}/pre";
            var postEndpoint = adv.PostEndpoint ?? $"/handlers/{handler.Id}/post";
            var proxyHttp = new HttpClient { BaseAddress = baseUri };
            var bindingDescriptor = new HandlerBindingDescriptor(
                manifest.Id, manifest.Version, handler.Id, handler.Seam, "container");
            var proxy = new HttpContainerHandlerProxy(
                proxyHttp, preEndpoint, postEndpoint, handler.FailureMode, bindingDescriptor, _logger);

            if (!SeamAdapters.TryGetValue(handler.Seam, out var adapterFactory))
                throw new NotSupportedException($"Seam '{handler.Seam}' not yet supported in container extensions.");
            object instance = adapterFactory(proxy);

            bindings.Add(new HandlerBinding(handler.Id, handler.Seam, handler.Priority, handler.FailureMode, instance));
        }

        // 5. Atomically swap registry, invalidate composer cache.
        var descriptor = new ExtensionDescriptor(
            ExtensionId: manifest.Id,
            Version:     manifest.Version,
            Manifest:    manifest,
            Handlers:    bindings,
            LoadContext: null); // container extension: no ALC

        var swapped = await _registry.SwapAsync(manifest.Id, descriptor, ct).ConfigureAwait(false);
        _composer.InvalidateAll();

        _logger.LogInformation(
            "container-ext-apply-success: {Id} v{Version} ({Count} handler(s)) at {BaseUri}",
            manifest.Id, manifest.Version, bindings.Count, baseUri);

        return new ExtensionReloadResult(swapped, descriptor, ExtensionReloadStatus.Success, null, null);
    }

    /// <summary>
    /// Remove a <c>host: container</c> extension: stop the container and drop registry entry.
    /// </summary>
    public async Task<ExtensionUnloadResult> RemoveAsync(string extensionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);

        var removed = await _registry.RemoveAsync(extensionId, ct).ConfigureAwait(false);
        if (removed is null)
            return new ExtensionUnloadResult(extensionId, null, ExtensionUnloadStatus.NotFound, null);

        _composer.InvalidateAll();

        try
        {
            await _containerHost.StopAsync(extensionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "container-ext-stop-failed: {Id}", extensionId);
        }

        _logger.LogInformation("container-ext-remove-success: {Id}", extensionId);
        return new ExtensionUnloadResult(extensionId, removed, ExtensionUnloadStatus.Success, null);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static Uri? FormBaseUri(ExtensionManifest manifest)
    {
        var port = manifest.Spec.Port;
        if (port is null or <= 0) return null;
        return new Uri($"http://localhost:{port}");
    }

    private static ExtensionReloadResult Failure(ExtensionDescriptor? old, string urn, Exception? ex) =>
        new(old, null, ExtensionReloadStatus.LoadFailed, urn, ex);
}

/// <summary>Handler advertisement returned by container's <c>GET /v1/handlers</c>.</summary>
internal sealed record ContainerHandlerAdvertisement(
    string ExtensionId,
    string Version,
    string TargetApiVersion,
    IReadOnlyList<AdvertisedHandler> Handlers);

internal sealed record AdvertisedHandler(
    string Id,
    string Seam,
    string? PreEndpoint,
    string? PostEndpoint);
