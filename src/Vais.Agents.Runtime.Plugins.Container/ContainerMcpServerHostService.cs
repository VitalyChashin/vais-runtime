// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Docker.DotNet;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Vais.Agents.Control;
using Vais.Agents.Control.Mcp;
using Vais.Agents.Core;
using Vais.Agents.Protocols.Mcp;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Hosted service that bridges <see cref="IMcpServerRegistry"/> entries with
/// <c>Transport == "containerStdio"</c> to live <see cref="McpClient"/> connections.
/// For each such server, the runtime supervises a container (via
/// <see cref="DockerContainerSupervisor"/> or <see cref="KubernetesContainerSupervisor"/>)
/// that exposes the stdio MCP child behind a thin streamableHttp bridge.
/// </summary>
/// <remarks>
/// <para>
/// Exposes connections via <see cref="INamedToolSourceProvider"/> so
/// <c>AgentManifestTranslator</c> finds them alongside <see cref="PhysicalMcpConnectionService"/>
/// (which handles non-container transports).
/// </para>
/// <para>
/// Plan: <c>plans/mcp-stdio-native-impl-2026-05-17.md</c>, tasks CMS-2 and CMS-3.
/// </para>
/// </remarks>
internal sealed class ContainerMcpServerHostService : BackgroundService, INamedToolSourceProvider, IContainerMcpServerHost
{
    internal const string ContainerStdioTransport = "containerStdio";

    private sealed class Entry : IAsyncDisposable
    {
        public required IContainerSupervisor Supervisor { get; init; }
        public required McpClient Client { get; init; }
        public required IAsyncDisposable Transport { get; init; }
        public required McpToolSource Source { get; init; }

        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            try { await Transport.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            try { await Supervisor.StopAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            try { await Supervisor.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    private readonly IMcpServerRegistry _registry;
    private readonly ContainerPluginLoaderOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ContainerMcpServerHostService> _logger;
    private readonly Func<IEnumerable<IMcpServerConnectionChangedHook>> _hooksFactory;
    private readonly Func<IDockerClient> _dockerFactory;
    private readonly Func<IKubernetes> _kubernetesFactory;

    // null value = tracked but not yet connected / reconnecting
    private readonly ConcurrentDictionary<string, Entry?> _entries = new(StringComparer.Ordinal);

    /// <summary>DI ctor.</summary>
    public ContainerMcpServerHostService(
        IMcpServerRegistry registry,
        ContainerPluginLoaderOptions options,
        IServiceProvider serviceProvider,
        ILoggerFactory loggerFactory)
        : this(registry, options,
              () => serviceProvider.GetServices<IMcpServerConnectionChangedHook>(),
              () => new DockerClientConfiguration().CreateClient(),
              () => new Kubernetes(KubernetesClientConfiguration.BuildDefaultConfig()),
              loggerFactory)
    {
    }

    /// <summary>Test ctor — explicit factories.</summary>
    internal ContainerMcpServerHostService(
        IMcpServerRegistry registry,
        ContainerPluginLoaderOptions options,
        Func<IEnumerable<IMcpServerConnectionChangedHook>> hooksFactory,
        Func<IDockerClient> dockerFactory,
        Func<IKubernetes> kubernetesFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _registry = registry;
        _options = options;
        _hooksFactory = hooksFactory;
        _dockerFactory = dockerFactory;
        _kubernetesFactory = kubernetesFactory;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<ContainerMcpServerHostService>();
    }

    /// <inheritdoc />
    public IToolSource? GetByName(string name)
    {
        _entries.TryGetValue(name, out var entry);
        return entry?.Source;
    }

    /// <inheritdoc />
    public async ValueTask RegisterAsync(McpServerManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!IsSupported(manifest))
            return;

        // If we already have a connection for this id, drop it before starting fresh.
        if (_entries.TryGetValue(manifest.Id, out var existing) && existing is not null)
        {
            _entries[manifest.Id] = null;
            await DispatchHooksAsync(h => h.OnDisconnectedAsync(manifest.Id, ct), manifest.Id).ConfigureAwait(false);
            await existing.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _entries.TryAdd(manifest.Id, null);
        }

        await ConnectOneAsync(manifest, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask UnregisterAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!_entries.TryRemove(id, out var entry) || entry is null)
            return;
        await DispatchHooksAsync(h => h.OnDisconnectedAsync(id, ct), id).ConfigureAwait(false);
        await entry.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial scan
        await foreach (var srv in _registry.ListAsync(ct: stoppingToken).ConfigureAwait(false))
        {
            if (!IsSupported(srv)) continue;
            _entries.TryAdd(srv.Id, null);
            _ = ConnectOneAsync(srv, stoppingToken);
        }

        // Reconnect loop: pick up new registrations and retry failed connections.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await foreach (var srv in _registry.ListAsync(ct: stoppingToken).ConfigureAwait(false))
            {
                if (!IsSupported(srv)) continue;
                _entries.TryAdd(srv.Id, null);
                if (_entries[srv.Id] is null)
                    _ = ConnectOneAsync(srv, stoppingToken);
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (id, entry) in _entries)
        {
            if (entry is null) continue;
            try
            {
                await DispatchHooksAsync(h => h.OnDisconnectedAsync(id, cancellationToken), id).ConfigureAwait(false);
                await entry.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping container MCP server '{ServerId}'", id);
            }
        }
        _entries.Clear();
    }

    private async Task ConnectOneAsync(McpServerManifest srv, CancellationToken ct)
    {
        try
        {
            var descriptor = ManifestToDescriptor(srv);
            var supervisor = CreateSupervisor(descriptor);

            try
            {
                await supervisor.StartAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await supervisor.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // The supervisor has polled the container's /health and confirmed Ready.
            // Open an McpClient over streamableHttp at the container's bridge endpoint.
            var endpoint = new Uri(descriptor.InvokeBaseUrl.TrimEnd('/') + srv.Container!.Path);
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                });

            McpClient client;
            try
            {
                client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
            }
            catch
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                await supervisor.StopAsync(ct).ConfigureAwait(false);
                await supervisor.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var entry = new Entry
            {
                Supervisor = supervisor,
                Client = client,
                Transport = transport,
                Source = new McpToolSource(client),
            };
            _entries[srv.Id] = entry;

            _logger.LogInformation(
                "Container MCP server connected. ServerId={ServerId} Image={Image} Endpoint={Endpoint}",
                srv.Id, descriptor.Image, endpoint);

            await DispatchHooksAsync(h => h.OnConnectedAsync(srv.Id, ct), srv.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Container MCP server connection failed. ServerId={ServerId} Image={Image}",
                srv.Id, srv.Container?.Image ?? "(build)");
            // Leave entry null so the reconnect loop retries.
        }
    }

    private IContainerSupervisor CreateSupervisor(ContainerPluginDescriptor descriptor)
    {
        if (descriptor.KubernetesConfig is not null)
        {
            var k8s = _kubernetesFactory();
            return new KubernetesContainerSupervisor(
                descriptor, k8s, _loggerFactory.CreateLogger<KubernetesContainerSupervisor>());
        }
        var docker = _dockerFactory();
        // OTLP wiring deferred for container MCP servers (see plan §10.4 option b) —
        // pass callTokenService=null and otlpEndpointUrl=null so the supervisor skips OTLP env injection.
        return new DockerContainerSupervisor(
            descriptor, docker, _loggerFactory.CreateLogger<DockerContainerSupervisor>(),
            callTokenService: null, otlpEndpointUrl: null);
    }

    private ContainerPluginDescriptor ManifestToDescriptor(McpServerManifest srv)
    {
        var c = srv.Container!;
        var bounds = _options.ResourceBounds;
        var image = c.Image ?? throw new InvalidOperationException(
            $"Container MCP server '{srv.Id}' has no image. Use `vais apply` build-on-apply, or pre-build and set `spec.container.image`.");

        ContainerTopology topology;
        KubernetesPluginConfig? k8s = null;
        string invokeBaseUrl;
        string? dockerNetwork = null;

        if (c.Kubernetes is { } k8sSpec)
        {
            topology = ContainerTopology.Kubernetes;
            k8s = new KubernetesPluginConfig(k8sSpec.ServiceUrl, k8sSpec.DeploymentName, k8sSpec.Namespace);
            invokeBaseUrl = k8sSpec.ServiceUrl;
        }
        else
        {
            topology = ContainerTopology.Standalone;
            dockerNetwork = _options.PluginNetwork;
            invokeBaseUrl = DockerNaming.InvokeUrl(srv.Id, c.Port, _options.PluginNetwork);
        }

        var secrets = new Dictionary<string, string>();
        if (c.Env is not null)
            foreach (var (k, v) in c.Env) secrets[k] = v;
        if (c.Secrets is not null)
            foreach (var (k, v) in c.Secrets) secrets[k] = v;

        return new ContainerPluginDescriptor
        {
            Name = srv.Id,
            Image = image,
            Port = c.Port,
            Topology = topology,
            StartupTimeoutSeconds = c.StartupTimeoutSeconds,
            InvokeTimeoutSeconds = 60,
            SecretRefs = secrets,
            InvokeBaseUrl = invokeBaseUrl,
            KubernetesConfig = k8s,
            DockerPluginNetwork = dockerNetwork,
            MemoryBytes = ContainerPluginResourceParser.Clamp(
                ContainerPluginResourceParser.ParseMemoryBytes(c.Resources?.Memory), bounds.MaxMemoryBytes),
            NanoCpus = ContainerPluginResourceParser.Clamp(
                ContainerPluginResourceParser.ParseNanoCpus(c.Resources?.Cpu), bounds.MaxNanoCpus),
            PidsLimit = ContainerPluginResourceParser.Clamp(
                c.Resources?.PidsLimit, bounds.MaxPidsLimit),
        };
    }

    private static bool IsSupported(McpServerManifest srv)
        => !srv.Virtual
            && string.Equals(srv.Transport, ContainerStdioTransport, StringComparison.Ordinal)
            && srv.Container is not null;

    private async Task DispatchHooksAsync(
        Func<IMcpServerConnectionChangedHook, Task> action, string serverId)
    {
        foreach (var hook in _hooksFactory())
        {
            try { await action(hook).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Container MCP server hook {HookType} threw for server {ServerId}",
                    hook.GetType().Name, serverId);
            }
        }
    }
}

/// <summary>
/// Surface used by the McpServer lifecycle manager to imperatively register / unregister
/// container-MCP servers in response to <c>vais apply</c> / <c>vais delete</c> calls.
/// Mirrors <see cref="IContainerPluginHost"/> for the plugin path.
/// </summary>
public interface IContainerMcpServerHost
{
    /// <summary>Start (or replace) the supervised container for this server and connect.</summary>
    ValueTask RegisterAsync(McpServerManifest manifest, CancellationToken ct = default);

    /// <summary>Stop and remove the supervised container; disconnect the MCP client.</summary>
    ValueTask UnregisterAsync(string id, CancellationToken ct = default);
}
