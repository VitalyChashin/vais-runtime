// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Default implementation of <see cref="IExtensionReloader"/>. Loads the new extension
/// into a fresh <see cref="ExtensionAssemblyLoadContext"/>, atomically swaps the handler
/// registry, invalidates the chain composer cache, then invokes registered
/// <see cref="IExtensionReloadHook"/> observers. Finally unloads the old ALC.
/// </summary>
internal sealed class DefaultExtensionReloader : IExtensionReloader
{
    private readonly ExtensionAssemblyLoader _loader;
    private readonly ExtensionHandlerRegistry _registry;
    private readonly IExtensionChainComposer _composer;
    private readonly ExtensionLoaderOptions _options;
    private readonly IExtensionReloadHook[] _hooks;
    private readonly ILogger<DefaultExtensionReloader> _logger;

    internal DefaultExtensionReloader(
        ExtensionAssemblyLoader loader,
        ExtensionHandlerRegistry registry,
        IExtensionChainComposer composer,
        ExtensionLoaderOptions? options = null,
        IEnumerable<IExtensionReloadHook>? hooks = null,
        ILogger<DefaultExtensionReloader>? logger = null)
    {
        _loader = loader;
        _registry = registry;
        _composer = composer;
        _options = options ?? new ExtensionLoaderOptions();
        _hooks = hooks?.OrderBy(h => h.Order).ToArray() ?? [];
        _logger = logger ?? NullLogger<DefaultExtensionReloader>.Instance;
    }

    /// <inheritdoc />
    public async Task<ExtensionReloadResult> ReloadAsync(
        ExtensionManifest manifest,
        Stream? dllStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!string.Equals(manifest.Spec.Host, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "reload-skip: extension '{Id}' host='{Host}' is not supported in Phase A.",
                manifest.Id, manifest.Spec.Host);
            return Failure(null, ExtensionUrns.ExtensionLoadFailed, null);
        }

        if (dllStream is null)
        {
            _logger.LogWarning("reload-failed: extension '{Id}' host=csharp requires a dll stream.", manifest.Id);
            return Failure(null, ExtensionUrns.ExtensionLoadFailed, null);
        }

        _logger.LogInformation("reload-begin: extension '{Id}' v{Version}", manifest.Id, manifest.Version);

        _registry.Snapshot().TryGetValue(manifest.Id, out var oldDescriptor);

        // Stage the DLL to a temp path so AssemblyDependencyResolver can find adjacent .deps.json.
        var tempDir = Path.Combine(Path.GetTempPath(), $"vais-ext-{manifest.Id}-{Guid.NewGuid():N}");
        var tempPath = Path.Combine(tempDir, $"{manifest.Id}.dll");
        ExtensionDescriptor? newDescriptor;
        try
        {
            Directory.CreateDirectory(tempDir);
            var bytes = await ReadToEndAsync(dllStream, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);

            using var loadStream = new MemoryStream(bytes); // bypass path-based assembly cache
            newDescriptor = _loader.Load(manifest, loadStream, tempPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "reload-failed: extension '{Id}' threw during load — {Urn}",
                manifest.Id, ExtensionUrns.ExtensionReloadFailed);
            TryCleanDir(tempDir);
            return Failure(oldDescriptor, ExtensionUrns.ExtensionReloadFailed, ex);
        }

        if (newDescriptor is null)
        {
            TryCleanDir(tempDir);
            return Failure(oldDescriptor, ExtensionUrns.ExtensionReloadFailed, null);
        }

        var swappedOld = await _registry.SwapAsync(manifest.Id, newDescriptor, cancellationToken).ConfigureAwait(false);

        _composer.InvalidateAll();

        _logger.LogInformation(
            "reload-success: extension '{Id}' v{Version} ({Count} handler(s)).",
            manifest.Id, manifest.Version, newDescriptor.Handlers.Count);

        var result = new ExtensionReloadResult(swappedOld, newDescriptor, ExtensionReloadStatus.Success, null, null);

        await DispatchHooksAsync(result, cancellationToken).ConfigureAwait(false);

        if (swappedOld?.LoadContext is { IsCollectible: true } oldAlc)
        {
            var alcRef = _options.DiagnoseUnloadLeaks ? new WeakReference(oldAlc) : null;
            oldAlc.Unload();
            if (alcRef is not null)
            {
                _ = MonitorUnloadAsync(alcRef, manifest.Id);
            }
        }

        TryCleanDir(tempDir);
        return result;
    }

    /// <inheritdoc />
    public async Task<ExtensionUnloadResult> UnloadAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);

        var removed = await _registry.RemoveAsync(extensionId, cancellationToken).ConfigureAwait(false);
        if (removed is null)
        {
            return new ExtensionUnloadResult(extensionId, null, ExtensionUnloadStatus.NotFound, null);
        }

        _composer.InvalidateAll();

        _logger.LogInformation("unload-success: extension '{Id}'", extensionId);

        if (removed.LoadContext is { IsCollectible: true } alc)
        {
            var alcRef = _options.DiagnoseUnloadLeaks ? new WeakReference(alc) : null;
            alc.Unload();
            if (alcRef is not null)
            {
                _ = MonitorUnloadAsync(alcRef, extensionId);
            }
        }

        return new ExtensionUnloadResult(extensionId, removed, ExtensionUnloadStatus.Success, null);
    }

    private async Task MonitorUnloadAsync(WeakReference alcRef, string extensionId)
    {
        const int pollIntervalMs = 2_000;
        const int maxPolls = 15; // 30 s
        for (int i = 0; i < maxPolls && alcRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(pollIntervalMs).ConfigureAwait(false);
        }

        if (alcRef.IsAlive)
        {
            _logger.LogWarning(
                "extension-unload-leak: AssemblyLoadContext for extension '{Id}' is still alive after {Seconds}s. " +
                "A live object reference is preventing GC.",
                extensionId, maxPolls * pollIntervalMs / 1000);
        }
    }

    private async Task DispatchHooksAsync(ExtensionReloadResult result, CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks)
        {
            try
            {
                await hook.OnReloadedAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Extension reload hook '{Hook}' threw after swap — continuing.",
                    hook.GetType().Name);
            }
        }
    }

    private static ExtensionReloadResult Failure(ExtensionDescriptor? old, string urn, Exception? ex) =>
        new(old, null, ExtensionReloadStatus.LoadFailed, urn, ex);

    private static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var buf = new MemoryStream();
        await stream.CopyToAsync(buf, cancellationToken).ConfigureAwait(false);
        return buf.ToArray();
    }

    private static void TryCleanDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
