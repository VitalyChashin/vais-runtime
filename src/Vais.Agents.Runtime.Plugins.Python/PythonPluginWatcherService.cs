// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Watches each loaded Python plugin's directory for file changes and triggers
/// <see cref="IPythonPluginReloader.ReloadAsync"/> with a per-plugin 200 ms debounce.
/// Watches <c>plugin.yaml</c>, <c>pyproject.toml</c>, and <c>*.py</c> files;
/// changes inside <c>.venv/</c> subdirectories are ignored.
/// Registered as <see cref="IHostedService"/> when
/// <see cref="PythonPluginLoaderOptions.ReloadPolicy"/> is
/// <see cref="ReloadPolicy.DrainAndSwap"/>.
/// The watcher starts only after <see cref="IHostApplicationLifetime.ApplicationStarted"/>
/// so all supervisors are online before any reload can be triggered.
/// </summary>
internal sealed class PythonPluginWatcherService : IHostedService, IDisposable
{
    private readonly IPythonPluginReloader _reloader;
    private readonly IPythonPluginHost _host;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PythonPluginWatcherService> _logger;
    private readonly TimeSpan _debounceDelay;

    // One watcher + debounce CTS per plugin directory.
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, CancellationTokenSource> _debounceCts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _debounceLock = new();

    internal PythonPluginWatcherService(
        IPythonPluginReloader reloader,
        IPythonPluginHost host,
        IHostApplicationLifetime lifetime,
        ILogger<PythonPluginWatcherService>? logger = null,
        TimeSpan? debounceDelay = null)
    {
        _reloader = reloader;
        _host = host;
        _lifetime = lifetime;
        _logger = logger ?? NullLogger<PythonPluginWatcherService>.Instance;
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(200);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(StartWatchers);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopWatchers();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => StopWatchers();

    private void StartWatchers()
    {
        var plugins = _host.LoadedPlugins;

        if (plugins.Count == 0)
        {
            _logger.LogInformation(
                "Python plugin watcher: no loaded plugins — not watching.");
            return;
        }

        foreach (var plugin in plugins)
        {
            var dir = plugin.Descriptor.PluginDirectory;
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning(
                    "Python plugin watcher: directory '{Dir}' does not exist for plugin '{Name}' — skipping.",
                    dir, plugin.Descriptor.Name);
                continue;
            }

            var watcher = new FileSystemWatcher(dir)
            {
                Filter = "*.*",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };

            watcher.Changed += (_, e) => OnFileChanged(e.FullPath, dir);
            watcher.Created += (_, e) => OnFileChanged(e.FullPath, dir);
            watcher.Error += OnWatcherError;

            lock (_watchers) _watchers.Add(watcher);

            _logger.LogInformation(
                "Python plugin watcher: watching '{Dir}' for plugin '{Name}' (debounce={D}ms).",
                dir, plugin.Descriptor.Name, _debounceDelay.TotalMilliseconds);
        }
    }

    private void StopWatchers()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }

        lock (_debounceLock)
        {
            foreach (var cts in _debounceCts.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _debounceCts.Clear();
        }
    }

    private void OnFileChanged(string fullPath, string pluginDirectory)
    {
        if (!IsReloadTrigger(fullPath))
            return;

        ScheduleReload(pluginDirectory);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        if (ex is InternalBufferOverflowException)
        {
            _logger.LogWarning(
                "Python plugin watcher: FileSystemWatcher buffer overflow — some change events " +
                "may have been lost. Consider reducing file churn in plugin directories.");
        }
        else
        {
            _logger.LogError(ex, "Python plugin watcher: FileSystemWatcher error.");
        }
    }

    private static bool IsReloadTrigger(string path)
    {
        // Skip anything inside a .venv directory — those can contain thousands of .py
        // files that change during a venv sync and should never trigger a reload.
        var sep = Path.DirectorySeparatorChar;
        var altSep = Path.AltDirectorySeparatorChar;
        if (path.Contains($"{sep}.venv{sep}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{altSep}.venv{altSep}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{sep}.venv{altSep}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{altSep}.venv{sep}", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var filename = Path.GetFileName(path.AsSpan());
        return filename.Equals("plugin.yaml", StringComparison.OrdinalIgnoreCase) ||
               filename.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleReload(string pluginDirectory)
    {
        CancellationToken token;
        lock (_debounceLock)
        {
            if (_debounceCts.TryGetValue(pluginDirectory, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            var cts = new CancellationTokenSource();
            _debounceCts[pluginDirectory] = cts;
            token = cts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounceDelay, token).ConfigureAwait(false);
                await HandleFileChangeAsync(pluginDirectory, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Debounce superseded by a subsequent change — expected.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Python plugin watcher: unhandled error while reloading '{Dir}'.",
                    pluginDirectory);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Triggers a reload for the given plugin directory. Internal for unit-test injection
    /// without a real <see cref="FileSystemWatcher"/>.
    /// </summary>
    internal async Task HandleFileChangeAsync(string pluginDirectory, CancellationToken ct)
    {
        _logger.LogInformation(
            "Python plugin watcher: change detected in '{Dir}' — triggering reload.",
            pluginDirectory);

        var result = await _reloader.ReloadAsync(pluginDirectory, ct).ConfigureAwait(false);

        if (result.Status == PythonPluginReloadStatus.Success)
        {
            _logger.LogInformation(
                "Python plugin watcher: reload succeeded for plugin '{Name}'.",
                result.PluginName);
        }
        else
        {
            _logger.LogWarning(
                "Python plugin watcher: reload of '{Dir}' completed with status {Status} — {Urn}.",
                pluginDirectory, result.Status, result.FailureUrn);
        }
    }
}
