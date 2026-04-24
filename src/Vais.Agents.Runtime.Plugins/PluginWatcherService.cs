// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Watches the plugins directory for DLL changes and triggers
/// <see cref="IPluginReloader.ReloadAsync"/> with a 200 ms debounce.
/// Registered as <see cref="IHostedService"/> when
/// <see cref="ReloadPolicy.DrainAndSwap"/> is configured.
/// The watcher only starts after <see cref="IHostApplicationLifetime.ApplicationStarted"/>
/// so all other services are fully online before a reload can occur.
/// </summary>
internal sealed class PluginWatcherService : IHostedService, IDisposable
{
    private readonly IPluginReloader _reloader;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PluginWatcherService> _logger;
    private readonly string _pluginsDirectory;
    private readonly TimeSpan _debounceDelay;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();

    internal PluginWatcherService(
        IPluginReloader reloader,
        IHostApplicationLifetime lifetime,
        string pluginsDirectory,
        ILogger<PluginWatcherService>? logger = null,
        TimeSpan? debounceDelay = null)
    {
        _reloader = reloader;
        _lifetime = lifetime;
        _pluginsDirectory = pluginsDirectory;
        _logger = logger ?? NullLogger<PluginWatcherService>.Instance;
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(200);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(StartWatcher);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopWatcher();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => StopWatcher();

    private void StartWatcher()
    {
        if (!Directory.Exists(_pluginsDirectory))
        {
            _logger.LogInformation(
                "Plugin watcher: directory '{Dir}' does not exist — not watching.",
                _pluginsDirectory);
            return;
        }

        _watcher = new FileSystemWatcher(_pluginsDirectory)
        {
            Filter = "*.dll",
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Error += OnWatcherError;

        _logger.LogInformation(
            "Plugin watcher: watching '{Dir}' for *.dll changes (debounce={Debounce}ms).",
            _pluginsDirectory,
            _debounceDelay.TotalMilliseconds);
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;

        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) =>
        ScheduleReload(e.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        if (ex is InternalBufferOverflowException)
        {
            _logger.LogWarning(
                "Plugin watcher: FileSystemWatcher buffer overflow — some change events may have been lost. Consider reducing plugin churn or increasing the OS notify buffer.");
        }
        else
        {
            _logger.LogError(ex, "Plugin watcher: FileSystemWatcher error.");
        }
    }

    private void ScheduleReload(string fullPath)
    {
        CancellationToken token;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceDelay, token).ConfigureAwait(false);
                await HandleFileChangeAsync(fullPath, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Debounce superseded by a subsequent change — expected.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Plugin watcher: unhandled error while reloading '{Path}'.",
                    fullPath);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Triggers a reload for the given path. Internal for unit-test injection
    /// without a real <see cref="FileSystemWatcher"/>.
    /// </summary>
    internal async Task HandleFileChangeAsync(string fullPath, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Plugin watcher: change detected for '{Path}' — triggering reload.",
            fullPath);

        var result = await _reloader.ReloadAsync(fullPath, cancellationToken).ConfigureAwait(false);

        if (result.Status == PluginReloadStatus.Success)
        {
            _logger.LogInformation(
                "Plugin watcher: reload succeeded for plugin '{Plugin}'.",
                result.NewDescriptor?.Name);
        }
        else
        {
            _logger.LogWarning(
                "Plugin watcher: reload of '{Path}' completed with status {Status} — {Urn}.",
                fullPath, result.Status, result.FailureUrn);
        }
    }
}
