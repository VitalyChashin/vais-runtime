// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Per-plugin state machine: spawn subprocess → MCP handshake → Ready → restart on crash.
/// Internal; one instance per <see cref="PythonPluginDescriptor"/>.
/// </summary>
internal sealed class PythonSubprocessSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan[] DefaultBackoffDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly TimeSpan[] _backoffDelays;

    private readonly PythonPluginDescriptor _descriptor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Func<PythonPluginDescriptor, ISubprocessHandle> _handleFactory;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource<bool> _initialHandshakeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _stateLock = new();
    private PythonPluginStatus _status = PythonPluginStatus.Loading;
    private int? _processId;
    private McpClient? _mcpClient;
    private Task _lifecycleTask = Task.CompletedTask;

    /// <summary>Current supervisor status (snapshot; may change immediately after read).</summary>
    internal PythonPluginStatus Status { get { lock (_stateLock) return _status; } }

    /// <summary>OS PID of the live subprocess, or <see langword="null"/>.</summary>
    internal int? ProcessId { get { lock (_stateLock) return _processId; } }

    /// <summary>The descriptor this supervisor was created for.</summary>
    internal PythonPluginDescriptor Descriptor => _descriptor;

    /// <summary>Live MCP client when <see cref="Status"/> is <see cref="PythonPluginStatus.Ready"/>.</summary>
    internal McpClient? McpClient { get { lock (_stateLock) return _mcpClient; } }

    /// <summary>
    /// Completes (with <see langword="true"/> on success) when the initial MCP handshake
    /// finishes — regardless of whether it succeeded or failed. Awaited by
    /// <see cref="PythonPluginHostService"/> during <c>StartAsync</c> to bound startup parallelism.
    /// </summary>
    internal Task<bool> InitialHandshakeTask => _initialHandshakeTcs.Task;

    // Production constructor — uses RealSubprocessHandle
    internal PythonSubprocessSupervisor(PythonPluginDescriptor descriptor, ILoggerFactory? loggerFactory = null)
        : this(descriptor, loggerFactory, static d => new RealSubprocessHandle(d), backoffDelays: null) { }

    // Test constructor — visible via InternalsVisibleTo
    internal PythonSubprocessSupervisor(
        PythonPluginDescriptor descriptor,
        ILoggerFactory? loggerFactory,
        Func<PythonPluginDescriptor, ISubprocessHandle> handleFactory,
        TimeSpan[]? backoffDelays = null)
    {
        _descriptor = descriptor;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PythonSubprocessSupervisor>();
        _handleFactory = handleFactory;
        _backoffDelays = backoffDelays ?? DefaultBackoffDelays;
    }

    /// <summary>
    /// Fires the lifecycle task in the background. Returns immediately; await
    /// <see cref="InitialHandshakeTask"/> to know when the first handshake completes.
    /// </summary>
    internal void Start() => _lifecycleTask = RunLifecycleAsync(_stopCts.Token);

    /// <summary>
    /// Signals stop, gracefully shuts down the subprocess (MCP shutdown → wait 5 s → kill),
    /// and awaits the lifecycle task.
    /// </summary>
    internal async Task StopAsync()
    {
        _stopCts.Cancel();
        _initialHandshakeTcs.TrySetResult(false); // unblock any still-pending handshake wait

        try { await _lifecycleTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Lifecycle task ended with an error during stop."); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    private async Task RunLifecycleAsync(CancellationToken ct)
    {
        // --- Initial spawn + handshake ---
        var (ok, handle, client) = await SpawnAndHandshakeAsync(ct).ConfigureAwait(false);

        if (!ok || ct.IsCancellationRequested)
        {
            if (handle is not null) await DisposeHandleAndClientAsync(client, handle).ConfigureAwait(false);
            SetState(PythonPluginStatus.Unavailable, null, null);
            _initialHandshakeTcs.TrySetResult(false);
            return;
        }

        SetState(PythonPluginStatus.Ready, handle!.ProcessId, client);
        _initialHandshakeTcs.TrySetResult(true);

        // --- Restart loop ---
        int restartAttempts = 0;

        while (!ct.IsCancellationRequested)
        {
            await WhenExitedOrStoppedAsync(handle!.Exited, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                // Graceful shutdown
                await DisposeHandleAndClientAsync(client, handle).ConfigureAwait(false);
                return;
            }

            // Process exited unexpectedly
            _logger.LogWarning(
                "[{Urn}] Python plugin '{Name}' (PID {Pid}) exited unexpectedly.",
                PythonPluginUrns.Exited, _descriptor.Name, handle!.ProcessId);

            var deadHandle = handle;
            handle = null; client = null;
            SetState(PythonPluginStatus.Loading, null, null);
            await DisposeHandleAndClientAsync(null, deadHandle).ConfigureAwait(false);

            // Check restart eligibility
            if (_descriptor.RestartPolicy == PythonRestartPolicy.Never)
            {
                _logger.LogWarning(
                    "[{Urn}] Python plugin '{Name}' is now unavailable (RestartPolicy=Never).",
                    PythonPluginUrns.Unavailable, _descriptor.Name);
                SetState(PythonPluginStatus.Unavailable, null, null);
                return;
            }

            if (restartAttempts >= _backoffDelays.Length)
            {
                _logger.LogWarning(
                    "[{Urn}] Python plugin '{Name}' is now unavailable after {N} restart attempt(s).",
                    PythonPluginUrns.Unavailable, _descriptor.Name, _backoffDelays.Length);
                SetState(PythonPluginStatus.Unavailable, null, null);
                return;
            }

            // Backoff
            SetState(PythonPluginStatus.Restarting, null, null);
            var delay = _backoffDelays[restartAttempts++];

            _logger.LogInformation(
                "Restarting Python plugin '{Name}' (attempt {Attempt}/{Max}) after {Delay}s backoff.",
                _descriptor.Name, restartAttempts, _backoffDelays.Length, delay.TotalSeconds);

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Respawn
            (ok, handle, client) = await SpawnAndHandshakeAsync(ct).ConfigureAwait(false);

            if (!ok || ct.IsCancellationRequested)
            {
                if (handle is not null) await DisposeHandleAndClientAsync(client, handle).ConfigureAwait(false);

                if (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "[{Urn}] Python plugin '{Name}' restart attempt {Attempt} failed; marking unavailable.",
                        PythonPluginUrns.Unavailable, _descriptor.Name, restartAttempts);
                    SetState(PythonPluginStatus.Unavailable, null, null);
                }
                return;
            }

            SetState(PythonPluginStatus.Ready, handle!.ProcessId, client);
        }
    }

    // -------------------------------------------------------------------------
    // Spawn + handshake
    // -------------------------------------------------------------------------

    private async Task<(bool Ok, ISubprocessHandle? Handle, McpClient? Client)> SpawnAndHandshakeAsync(
        CancellationToken stopToken)
    {
        ISubprocessHandle? handle = null;

        try
        {
            handle = _handleFactory(_descriptor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Urn}] Failed to spawn process for Python plugin '{Name}'.",
                PythonPluginUrns.LoadFailed, _descriptor.Name);
            return (false, null, null);
        }

        // Fire-and-forget stderr reader — completes when the process's stderr stream closes.
        _ = ForwardStderrAsync(handle.StandardError, _descriptor.Name, stopToken);

        // Bound the entire handshake (initialize + tools/list) by a single timeout CTS.
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        handshakeCts.CancelAfter(TimeSpan.FromSeconds(_descriptor.HandshakeTimeoutSeconds));

        McpClient? client = null;
        try
        {
            var transport = new StreamClientTransport(
                handle.StandardInput,
                handle.StandardOutput,
                _loggerFactory);

            var clientOptions = new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "vais-agents-runtime",
                    Version = PythonPluginAbi.CurrentVersion,
                },
            };

            client = await McpClient.CreateAsync(transport, clientOptions, _loggerFactory, handshakeCts.Token)
                .ConfigureAwait(false);

            var tools = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, handshakeCts.Token)
                .ConfigureAwait(false);

            VerifyDeclaredTools(tools);

            return (true, handle, client);
        }
        catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[{Urn}] MCP handshake timed out ({Seconds}s) for Python plugin '{Name}'.",
                PythonPluginUrns.HandshakeTimeout, _descriptor.HandshakeTimeoutSeconds, _descriptor.Name);
            if (client is not null)
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            handle.Kill();
            await handle.DisposeAsync().ConfigureAwait(false);
            return (false, null, null);
        }
        catch (OperationCanceledException)
        {
            // Stop signal during handshake — caller handles cleanup.
            if (client is not null)
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            handle.Kill();
            await handle.DisposeAsync().ConfigureAwait(false);
            return (false, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Urn}] MCP handshake failed for Python plugin '{Name}'.",
                PythonPluginUrns.LoadFailed, _descriptor.Name);
            if (client is not null)
                try { await client.DisposeAsync().ConfigureAwait(false); } catch { }
            handle.Kill();
            await handle.DisposeAsync().ConfigureAwait(false);
            return (false, null, null);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void VerifyDeclaredTools(IList<McpClientTool> serverTools)
    {
        if (_descriptor.DeclaredTools.Count == 0)
            return;

        var serverNames = serverTools.Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var declared in _descriptor.DeclaredTools)
        {
            if (!serverNames.Contains(declared))
            {
                _logger.LogWarning(
                    "Python plugin '{Name}': declared tool '{Tool}' was not found in the " +
                    "tools/list response (python-plugin-tool-mismatch). " +
                    "The pyproject.toml [tool.vais.plugin].tools list may be out of date.",
                    _descriptor.Name, declared);
            }
        }
    }

    private async Task ForwardStderrAsync(TextReader stderr, string pluginName, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                using (_logger.BeginScope(new Dictionary<string, object?> { ["plugin"] = pluginName }))
                    _logger.LogInformation("{Line}", line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Stderr reader for plugin '{Plugin}' ended with an error.", pluginName);
        }
    }

    private static async Task WhenExitedOrStoppedAsync(Task exitTask, CancellationToken stopToken)
    {
        var stopTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = stopToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), stopTcs);
        await Task.WhenAny(exitTask, stopTcs.Task).ConfigureAwait(false);
    }

    private static async Task DisposeHandleAndClientAsync(McpClient? client, ISubprocessHandle handle)
    {
        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }

        try { await handle.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    private void SetState(PythonPluginStatus status, int? processId, McpClient? mcpClient)
    {
        lock (_stateLock)
        {
            _status = status;
            _processId = processId;
            _mcpClient = mcpClient;
        }
    }
}
