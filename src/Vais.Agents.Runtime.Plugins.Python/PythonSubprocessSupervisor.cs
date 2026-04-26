// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Per-plugin state machine: spawn subprocess → MCP handshake → Ready → restart on crash.
/// Supports in-place hot-reload via <see cref="DrainAndRestartAsync"/> when
/// <see cref="PythonPluginLoaderOptions.ReloadPolicy"/> is
/// <see cref="ReloadPolicy.DrainAndSwap"/>.
/// Internal; one instance per <see cref="PythonPluginDescriptor"/>.
/// </summary>
internal sealed class PythonSubprocessSupervisor : IAsyncDisposable, IPythonAgentChannel
{
    private static readonly TimeSpan[] DefaultBackoffDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly TimeSpan[] _backoffDelays;

    // Original descriptor kept for type-name guard in DrainAndRestartAsync.
    private readonly PythonPluginDescriptor _descriptor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Func<PythonPluginDescriptor, ISubprocessHandle> _handleFactory;

    // _liveDescriptor is updated on each successful hot-reload cycle.
    // All spawning paths read this field rather than _descriptor so the new
    // interpreter path / timeout / etc. take effect immediately.
    private PythonPluginDescriptor _liveDescriptor;

    // _stopCts is replaced on each reload cycle; not readonly so DrainAndRestartAsync
    // can create a fresh one after stopping the old lifecycle.
    private CancellationTokenSource _stopCts = new();

    // Replaced each Start / DrainAndRestartAsync call so callers can await the
    // current handshake independently of prior cycles.
    private TaskCompletionSource<bool> _currentHandshakeTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Drain tracking ─────────────────────────────────────────────────────────
    // _activeInvokes is read/written with Interlocked; the increment is gated
    // under _stateLock so _draining=true prevents new acquisitions atomically.
    private int _activeInvokes = 0;
    private bool _draining = false;                  // under _stateLock
    private TaskCompletionSource? _drainSignal = null; // under _stateLock

    // Serializes concurrent hot-reload requests (debounce handles burst at the
    // file-watcher level but two CPU threads can still race here).
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private readonly object _stateLock = new();
    private PythonPluginStatus _status = PythonPluginStatus.Loading;
    private int? _processId;
    private McpClient? _mcpClient;
    private Task _lifecycleTask = Task.CompletedTask;

    // Rolling buffer of the last 20 stderr lines from the subprocess.
    // Reset on each spawn so timeout messages reflect the current process only.
    private ConcurrentQueue<string> _stderrTail = new();

    /// <summary>Current supervisor status (snapshot; may change immediately after read).</summary>
    internal PythonPluginStatus Status { get { lock (_stateLock) return _status; } }

    /// <summary>OS PID of the live subprocess, or <see langword="null"/>.</summary>
    internal int? ProcessId { get { lock (_stateLock) return _processId; } }

    /// <summary>The live descriptor (updated on hot-reload).</summary>
    public PythonPluginDescriptor Descriptor => _liveDescriptor;

    /// <summary>Live MCP client when <see cref="Status"/> is <see cref="PythonPluginStatus.Ready"/>.</summary>
    internal McpClient? McpClient { get { lock (_stateLock) return _mcpClient; } }

    /// <summary>
    /// Completes (with <see langword="true"/> on success) when the current MCP handshake
    /// finishes — regardless of whether it succeeded or failed. Awaited by
    /// <see cref="PythonPluginHostService"/> during <c>StartAsync</c> to bound startup parallelism.
    /// On hot-reload a new TCS is created per cycle; this property always returns the current one.
    /// </summary>
    internal Task<bool> InitialHandshakeTask => _currentHandshakeTcs.Task;

    // Production constructor — uses RealSubprocessHandle
    internal PythonSubprocessSupervisor(PythonPluginDescriptor descriptor, ILoggerFactory? loggerFactory = null)
        : this(descriptor, loggerFactory, static d => new RealSubprocessHandle(d), backoffDelays: null)
    {
    }

    // Test constructor — visible via InternalsVisibleTo
    internal PythonSubprocessSupervisor(
        PythonPluginDescriptor descriptor,
        ILoggerFactory? loggerFactory,
        Func<PythonPluginDescriptor, ISubprocessHandle> handleFactory,
        TimeSpan[]? backoffDelays = null)
    {
        _descriptor = descriptor;
        _liveDescriptor = descriptor;
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
        _currentHandshakeTcs.TrySetResult(false); // unblock any still-pending handshake wait

        try { await _lifecycleTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogDebug(ex, "Lifecycle task ended with an error during stop."); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopCts.Dispose();
        _reloadLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Hot-reload (v0.25)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Drains in-flight invokes, stops the current subprocess, spawns a new one
    /// with <paramref name="newDescriptor"/>, and returns when the handshake completes.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the new subprocess is Ready;
    /// <see langword="false"/> when <paramref name="newDescriptor"/> changes
    /// <c>HandlerTypeName</c> (refused — silo restart required) or when the new
    /// subprocess handshake fails.
    /// </returns>
    internal async Task<bool> DrainAndRestartAsync(
        PythonPluginDescriptor newDescriptor,
        TimeSpan drainTimeout,
        CancellationToken ct)
    {
        // Guard: HandlerTypeName change is not supported in-place.
        if (!string.Equals(
                newDescriptor.HandlerTypeName, _liveDescriptor.HandlerTypeName,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[{Urn}] Python plugin '{Name}' hot-reload refused: HandlerTypeName changed " +
                "from '{Old}' to '{New}' — silo restart required.",
                PythonPluginUrns.ReloadHandlerTypeNameChanged,
                _liveDescriptor.Name,
                _liveDescriptor.HandlerTypeName,
                newDescriptor.HandlerTypeName);
            return false;
        }

        await _reloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation(
                "python-reload-begin: plugin '{Name}' drain-timeout={Timeout}s.",
                _liveDescriptor.Name, (int)drainTimeout.TotalSeconds);

            // ── DRAIN ────────────────────────────────────────────────────────
            // Setting _draining under _stateLock prevents any new InvokeAgentAsync
            // from incrementing _activeInvokes.  Any invoke that already holds the
            // lock and incremented will decrement in its finally block and signal
            // _drainSignal when the count reaches zero.
            TaskCompletionSource drainSignal;
            lock (_stateLock)
            {
                _draining = true;
                drainSignal = _drainSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
            // If already zero (no concurrent invokes), complete immediately.
            if (Volatile.Read(ref _activeInvokes) == 0)
                drainSignal.TrySetResult();

            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainCts.CancelAfter(drainTimeout);
            try
            {
                await drainSignal.Task.WaitAsync(drainCts.Token).ConfigureAwait(false);
                _logger.LogDebug(
                    "python-reload: all in-flight invokes drained for '{Name}'.",
                    _liveDescriptor.Name);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "python-reload: drain timed out for '{Name}' — forcing reload.",
                    _liveDescriptor.Name);
            }

            // ── STOP OLD LIFECYCLE ───────────────────────────────────────────
            var oldStopCts = _stopCts;
            oldStopCts.Cancel();
            _currentHandshakeTcs.TrySetResult(false); // unblock anyone awaiting old handshake

            try { await _lifecycleTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Lifecycle ended with error during hot-reload of '{Name}'.",
                    _liveDescriptor.Name);
            }

            oldStopCts.Dispose();

            // Reset state — lifecycle task is complete so no concurrent modifications.
            lock (_stateLock)
            {
                _status = PythonPluginStatus.Loading;
                _processId = null;
                _mcpClient = null;
                _draining = false;
                _drainSignal = null;
            }
            Volatile.Write(ref _activeInvokes, 0);

            // ── START NEW LIFECYCLE ──────────────────────────────────────────
            _liveDescriptor = newDescriptor;
            _stopCts = new CancellationTokenSource();
            _currentHandshakeTcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _lifecycleTask = RunLifecycleAsync(_stopCts.Token);

            // ── AWAIT NEW HANDSHAKE ──────────────────────────────────────────
            bool ok = await _currentHandshakeTcs.Task.WaitAsync(ct).ConfigureAwait(false);

            if (ok)
                _logger.LogInformation(
                    "python-reload-success: plugin '{Name}'.", newDescriptor.Name);
            else
                _logger.LogWarning(
                    "[{Urn}] python-reload-failed: plugin '{Name}' handshake failed after reload.",
                    PythonPluginUrns.ReloadHandshakeFailed, newDescriptor.Name);

            return ok;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Agent invocation (v0.24)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Send a <c>vais/agent.invoke</c> JSON-RPC call to the subprocess and return
    /// its response. Throws <see cref="InvalidOperationException"/> when the
    /// supervisor is not <see cref="PythonPluginStatus.Ready"/> or is draining for
    /// a hot-reload, and <see cref="TimeoutException"/> when the call exceeds
    /// <see cref="PythonPluginDescriptor.InvokeTimeoutSeconds"/>.
    /// </summary>
    public async Task<AgentInvokeResponse> InvokeAgentAsync(
        AgentInvokeRequest request,
        CancellationToken ct)
    {
        McpClient? client;
        lock (_stateLock)
        {
            // Refuse when draining (reload in progress) or not Ready.
            if (_draining || _status != PythonPluginStatus.Ready)
                client = null;
            else
            {
                client = _mcpClient;
                // Increment while holding _stateLock so _draining=true cannot be
                // missed between the check and the increment.
                Interlocked.Increment(ref _activeInvokes);
            }
        }

        if (client is null)
        {
            _logger.LogWarning(
                "[{Urn}] Cannot invoke Python agent '{Name}' — supervisor status is {Status}.",
                PythonPluginUrns.Unavailable, _liveDescriptor.Name, Status);
            throw new InvalidOperationException(
                $"[{PythonPluginUrns.Unavailable}] Python plugin '{_liveDescriptor.Name}' is unavailable.");
        }

        using var invokeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        invokeCts.CancelAfter(TimeSpan.FromSeconds(_liveDescriptor.InvokeTimeoutSeconds));

        try
        {
            return await client.SendRequestAsync<AgentInvokeRequest, AgentInvokeResponse>(
                "vais/agent.invoke",
                request,
                AgentProtocolJson.Options,
                new RequestId(Guid.NewGuid().ToString()),
                invokeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[{Urn}] Python agent '{Name}' invoke timed out after {Seconds}s.",
                PythonPluginUrns.AgentInvokeTimeout, _liveDescriptor.Name, _liveDescriptor.InvokeTimeoutSeconds);
            throw new TimeoutException(
                $"[{PythonPluginUrns.AgentInvokeTimeout}] Python agent '{_liveDescriptor.Name}' " +
                $"invoke timed out after {_liveDescriptor.InvokeTimeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "[{Urn}] Python agent '{Name}' invoke failed.",
                PythonPluginUrns.AgentInvokeFailed, _liveDescriptor.Name);
            throw new InvalidOperationException(
                $"[{PythonPluginUrns.AgentInvokeFailed}] Python agent '{_liveDescriptor.Name}' " +
                $"invoke failed: {ex.Message}", ex);
        }
        finally
        {
            // Decrement and signal drain if this was the last in-flight invoke.
            if (Interlocked.Decrement(ref _activeInvokes) == 0)
            {
                TaskCompletionSource? signal;
                lock (_stateLock) signal = _drainSignal;
                signal?.TrySetResult();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Agent streaming (v0.26)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Send a <c>vais/agent.stream</c> JSON-RPC call and yield response frames.
    /// Delta frames (from <see cref="AgentInvokeResponse.Deltas"/>) carry
    /// <see cref="AgentStreamFrame.TextDelta"/>; the terminal frame carries
    /// <see cref="AgentStreamFrame.FinalResponse"/>.
    /// Deltas are bundled in the JSON-RPC response — no notification race.
    /// </summary>
    public async IAsyncEnumerable<AgentStreamFrame> StreamAgentAsync(
        AgentInvokeRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        McpClient? client;
        lock (_stateLock)
        {
            if (_draining || _status != PythonPluginStatus.Ready)
                client = null;
            else
            {
                client = _mcpClient;
                Interlocked.Increment(ref _activeInvokes);
            }
        }

        if (client is null)
        {
            _logger.LogWarning(
                "[{Urn}] Cannot stream Python agent '{Name}' — supervisor status is {Status}.",
                PythonPluginUrns.Unavailable, _liveDescriptor.Name, Status);
            throw new InvalidOperationException(
                $"[{PythonPluginUrns.Unavailable}] Python plugin '{_liveDescriptor.Name}' is unavailable.");
        }

        using var invokeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        invokeCts.CancelAfter(TimeSpan.FromSeconds(_liveDescriptor.InvokeTimeoutSeconds));

        AgentInvokeResponse response;
        try
        {
            response = await client.SendRequestAsync<AgentInvokeRequest, AgentInvokeResponse>(
                "vais/agent.stream",
                request,
                AgentProtocolJson.Options,
                new RequestId(Guid.NewGuid().ToString()),
                invokeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[{Urn}] Python agent '{Name}' stream timed out after {Seconds}s.",
                PythonPluginUrns.AgentInvokeTimeout, _liveDescriptor.Name, _liveDescriptor.InvokeTimeoutSeconds);
            if (Interlocked.Decrement(ref _activeInvokes) == 0)
            {
                TaskCompletionSource? signal;
                lock (_stateLock) signal = _drainSignal;
                signal?.TrySetResult();
            }
            throw new TimeoutException(
                $"[{PythonPluginUrns.AgentInvokeTimeout}] Python agent '{_liveDescriptor.Name}' " +
                $"stream timed out after {_liveDescriptor.InvokeTimeoutSeconds}s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "[{Urn}] Python agent '{Name}' stream failed.",
                PythonPluginUrns.AgentInvokeFailed, _liveDescriptor.Name);
            if (Interlocked.Decrement(ref _activeInvokes) == 0)
            {
                TaskCompletionSource? signal;
                lock (_stateLock) signal = _drainSignal;
                signal?.TrySetResult();
            }
            throw new InvalidOperationException(
                $"[{PythonPluginUrns.AgentInvokeFailed}] Python agent '{_liveDescriptor.Name}' " +
                $"stream failed: {ex.Message}", ex);
        }

        if (Interlocked.Decrement(ref _activeInvokes) == 0)
        {
            TaskCompletionSource? signal;
            lock (_stateLock) signal = _drainSignal;
            signal?.TrySetResult();
        }

        foreach (var delta in response.Deltas ?? [])
            yield return new AgentStreamFrame(delta, null);
        yield return new AgentStreamFrame(null, response);
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
            _currentHandshakeTcs.TrySetResult(false);
            return;
        }

        SetState(PythonPluginStatus.Ready, handle!.ProcessId, client);
        _currentHandshakeTcs.TrySetResult(true);

        // --- Restart loop ---
        int restartAttempts = 0;

        while (!ct.IsCancellationRequested)
        {
            await WhenExitedOrStoppedAsync(handle!.Exited, ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                // Graceful shutdown (StopAsync or DrainAndRestartAsync cancelled lifecycle).
                await DisposeHandleAndClientAsync(client, handle).ConfigureAwait(false);
                return;
            }

            // Process exited unexpectedly
            _logger.LogWarning(
                "[{Urn}] Python plugin '{Name}' (PID {Pid}) exited unexpectedly.",
                PythonPluginUrns.Exited, _liveDescriptor.Name, handle!.ProcessId);

            var deadHandle = handle;
            handle = null; client = null;
            SetState(PythonPluginStatus.Loading, null, null);
            await DisposeHandleAndClientAsync(null, deadHandle).ConfigureAwait(false);

            // Check restart eligibility
            if (_liveDescriptor.RestartPolicy == PythonRestartPolicy.Never)
            {
                _logger.LogWarning(
                    "[{Urn}] Python plugin '{Name}' is now unavailable (RestartPolicy=Never).",
                    PythonPluginUrns.Unavailable, _liveDescriptor.Name);
                SetState(PythonPluginStatus.Unavailable, null, null);
                return;
            }

            if (restartAttempts >= _backoffDelays.Length)
            {
                _logger.LogWarning(
                    "[{Urn}] Python plugin '{Name}' is now unavailable after {N} restart attempt(s).",
                    PythonPluginUrns.Unavailable, _liveDescriptor.Name, _backoffDelays.Length);
                SetState(PythonPluginStatus.Unavailable, null, null);
                return;
            }

            // Backoff
            SetState(PythonPluginStatus.Restarting, null, null);
            var delay = _backoffDelays[restartAttempts++];

            _logger.LogInformation(
                "Restarting Python plugin '{Name}' (attempt {Attempt}/{Max}) after {Delay}s backoff.",
                _liveDescriptor.Name, restartAttempts, _backoffDelays.Length, delay.TotalSeconds);

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
                        PythonPluginUrns.Unavailable, _liveDescriptor.Name, restartAttempts);
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

        // Use the live descriptor so hot-reloads pick up updated interpreter / entrypoint.
        var desc = _liveDescriptor;

        try
        {
            handle = _handleFactory(desc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Urn}] Failed to spawn process for Python plugin '{Name}'.",
                PythonPluginUrns.LoadFailed, desc.Name);
            return (false, null, null);
        }

        // Reset the stderr tail for this spawn cycle so timeout messages don't include
        // output from a prior crashed process.
        _stderrTail = new ConcurrentQueue<string>();

        // Fire-and-forget stderr reader — completes when the process's stderr stream closes.
        _ = ForwardStderrAsync(handle.StandardError, desc.Name, stopToken);

        // Bound the entire handshake (initialize + tools/list) by a single timeout CTS.
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        handshakeCts.CancelAfter(TimeSpan.FromSeconds(desc.HandshakeTimeoutSeconds));

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

            VerifyDeclaredTools(tools, desc);

            return (true, handle, client);
        }
        catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
        {
            var tail = _stderrTail.ToArray();
            var stderrSnippet = tail.Length > 0
                ? "\nLast subprocess output:\n" + string.Join("\n", tail)
                : " (no subprocess output captured)";
            _logger.LogWarning(
                "[{Urn}] MCP handshake timed out ({Seconds}s) for Python plugin '{Name}'.{Stderr}",
                PythonPluginUrns.HandshakeTimeout, desc.HandshakeTimeoutSeconds, desc.Name, stderrSnippet);
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
                PythonPluginUrns.LoadFailed, desc.Name);
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

    private void VerifyDeclaredTools(IList<McpClientTool> serverTools, PythonPluginDescriptor desc)
    {
        if (desc.DeclaredTools.Count == 0)
            return;

        var serverNames = serverTools.Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var declared in desc.DeclaredTools)
        {
            if (!serverNames.Contains(declared))
            {
                _logger.LogWarning(
                    "Python plugin '{Name}': declared tool '{Tool}' was not found in the " +
                    "tools/list response (python-plugin-tool-mismatch). " +
                    "The pyproject.toml [tool.vais.plugin].tools list may be out of date.",
                    desc.Name, declared);
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

                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > 20)
                    _stderrTail.TryDequeue(out _);
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
