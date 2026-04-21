// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Core;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// In-process <see cref="IAgentGraphLifecycleManager"/> backed by
/// <see cref="InProcessGraphOrchestrator{TState}"/>. Routes all graph verbs through
/// policy + audit middleware; delegates execution to a per-invocation orchestrator
/// instance so each run is isolated.
/// </summary>
/// <remarks>
/// <para>
/// <b>v0.19 scope.</b> Wires the bag-state path (<see cref="IDictionary{String,JsonElement}"/>)
/// only — code-first typed graphs require callers to use
/// <see cref="InProcessGraphOrchestrator{TState}"/> directly until a typed surface
/// lands in a follow-up pillar.
/// </para>
/// <para>
/// <b>Counters are in-process only.</b> <see cref="AgentGraphStatus"/> counters reset
/// on process restart. Production deployments should use the Orleans-backed manager
/// (PR 2) for durable counter tracking.
/// </para>
/// </remarks>
public sealed class AgentGraphLifecycleManager : IAgentGraphLifecycleManager
{
    private readonly IAgentGraphRegistry _graphRegistry;
    private readonly IAgentRegistry _agentRegistry;
    private readonly IAgentLifecycleManager _agentLifecycle;
    private readonly IGraphCheckpointer _checkpointer;
    private readonly IAgentPolicyEngine _policy;
    private readonly IAuditLog _audit;
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly ILogger<AgentGraphLifecycleManager> _logger;

    // Per-graph state: manifest-keyed counters + run-id-keyed CTS map.
    private readonly ConcurrentDictionary<(string Id, string Version), GraphEntry> _graphs = new();

    // Per-run state keyed by RunId for conflict detection + cancel.
    private readonly ConcurrentDictionary<string, RunEntry> _runs = new();

    /// <summary>Shared error code consumers can match on to translate to HTTP 404 etc.</summary>
    public const string UnknownGraphErrorType = "GraphHandleNotFound";

    /// <summary>Construct a manager. Graph-registry, agent-registry, agent-lifecycle, and checkpointer are required.</summary>
    public AgentGraphLifecycleManager(
        IAgentGraphRegistry graphRegistry,
        IAgentRegistry agentRegistry,
        IAgentLifecycleManager agentLifecycle,
        IGraphCheckpointer checkpointer,
        IAgentPolicyEngine? policy = null,
        IAuditLog? audit = null,
        IAgentContextAccessor? contextAccessor = null,
        ILogger<AgentGraphLifecycleManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(graphRegistry);
        ArgumentNullException.ThrowIfNull(agentRegistry);
        ArgumentNullException.ThrowIfNull(agentLifecycle);
        ArgumentNullException.ThrowIfNull(checkpointer);
        _graphRegistry = graphRegistry;
        _agentRegistry = agentRegistry;
        _agentLifecycle = agentLifecycle;
        _checkpointer = checkpointer;
        _policy = policy ?? NullAgentPolicyEngine.Instance;
        _audit = audit ?? NullAuditLog.Instance;
        _contextAccessor = contextAccessor ?? new AsyncLocalAgentContextAccessorFallback();
        _logger = logger ?? NullLogger<AgentGraphLifecycleManager>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<AgentGraphHandle> CreateAsync(AgentGraphManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.GraphCreate, manifest.Id, manifest.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RegisterManifest(manifest);
            _graphs.GetOrAdd((manifest.Id, manifest.Version), _ => new GraphEntry());
            return new AgentGraphHandle(manifest.Id, manifest.Version);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            sw.Stop();
            await AuditAsync(PolicyOperation.GraphCreate, manifest.Id, manifest.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AgentGraphHandle> UpdateAsync(AgentGraphHandle handle, AgentGraphManifest newManifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(newManifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.GraphUpdate, handle.GraphId, handle.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            RegisterManifest(newManifest);
            _graphs.GetOrAdd((newManifest.Id, newManifest.Version), _ => new GraphEntry());
            return new AgentGraphHandle(newManifest.Id, newManifest.Version);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.GraphUpdate, handle.GraphId, handle.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AgentGraphStatus> QueryAsync(AgentGraphHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.GraphQuery, handle.GraphId, handle.Version, principal, ct).ConfigureAwait(false);

        try
        {
            if (!_graphs.TryGetValue((handle.GraphId, handle.Version), out var entry))
            {
                throw new GraphHandleNotFoundException(handle.GraphId, handle.Version);
            }
            return entry.ToStatus(handle.GraphId, handle.Version);
        }
        finally
        {
            await AuditAsync(PolicyOperation.GraphQuery, handle.GraphId, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<GraphInvocationResult> InvokeAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var (manifest, entry, runId) = await PrepareInvocationAsync(handle, request, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runEntry = new RunEntry(handle.GraphId, cts);
        if (!_runs.TryAdd(runId, runEntry))
        {
            throw new GraphRunConflictException(handle.GraphId, runId);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? errorType = null;
        try
        {
            entry.RecordInvoke();
            var context = BuildContext(request.Metadata);
            var orchestrator = BuildOrchestrator(manifest, runId, request.MaxSteps);
            GraphInvocationResult result = await DrainInvokeAsync(orchestrator, request.InitialState, context, runId, cts.Token).ConfigureAwait(false);

            if (result.IsComplete)
            {
                entry.RecordComplete();
            }
            else
            {
                entry.RecordInterrupt();
            }
            return result;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            sw.Stop();
            _runs.TryRemove(runId, out _);
            if (errorType is not null) entry.RecordError();
            await AuditAsync(PolicyOperation.GraphInvoke, handle.GraphId, handle.Version, SynthesizePrincipal(), allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentGraphEvent> InvokeStreamAsync(
        AgentGraphHandle handle,
        GraphInvocationRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var (manifest, entry, runId) = await PrepareInvocationAsync(handle, request, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_runs.TryAdd(runId, new RunEntry(handle.GraphId, cts)))
        {
            throw new GraphRunConflictException(handle.GraphId, runId);
        }

        entry.RecordInvoke();
        bool completed = false;
        bool interrupted = false;
        try
        {
            var context = BuildContext(request.Metadata);
            var orchestrator = BuildOrchestrator(manifest, runId, request.MaxSteps);
            await foreach (var evt in orchestrator.StreamAsync(request.InitialState, context, cts.Token).ConfigureAwait(false))
            {
                if (evt is GraphCompleted) completed = true;
                if (evt is GraphInterrupted) interrupted = true;
                yield return evt;
            }
        }
        finally
        {
            _runs.TryRemove(runId, out _);
            if (completed) entry.RecordComplete();
            else if (interrupted) entry.RecordInterrupt();
            await AuditAsync(PolicyOperation.GraphInvoke, handle.GraphId, handle.Version, SynthesizePrincipal(), allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<GraphInvocationResult> ResumeAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var (manifest, entry, checkpoint) = await PrepareResumeAsync(handle, request, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runEntry = new RunEntry(handle.GraphId, cts);
        _runs[request.RunId] = runEntry;

        string? errorType = null;
        try
        {
            entry.RecordResumeFromInterrupt();
            var context = BuildContext(request.Metadata);
            var orchestrator = BuildOrchestrator(manifest, request.RunId, maxStepsOverride: null);
            var payload = request.ResumePayload;
            GraphInvocationResult result = await DrainResumeAsync(orchestrator, checkpoint, payload, context, request.RunId, cts.Token).ConfigureAwait(false);

            if (result.IsComplete) entry.RecordComplete();
            else entry.RecordInterrupt();
            return result;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            _runs.TryRemove(request.RunId, out _);
            if (errorType is not null) entry.RecordError();
            await AuditAsync(PolicyOperation.GraphResume, handle.GraphId, handle.Version, SynthesizePrincipal(), allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(
        AgentGraphHandle handle,
        GraphResumeRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var (manifest, entry, checkpoint) = await PrepareResumeAsync(handle, request, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runs[request.RunId] = new RunEntry(handle.GraphId, cts);

        entry.RecordResumeFromInterrupt();
        bool completed = false;
        bool interrupted = false;
        try
        {
            var context = BuildContext(request.Metadata);
            var orchestrator = BuildOrchestrator(manifest, request.RunId, maxStepsOverride: null);

            // Pre-merge resume payload under "resume.payload" so the orchestrator picks
            // it up without a second pass. Pass null to the orchestrator's resumePayload param.
            IReadOnlyDictionary<string, JsonElement> mergedState = checkpoint.State;
            if (request.ResumePayload.HasValue)
            {
                var stateWithPayload = new Dictionary<string, JsonElement>(checkpoint.State, StringComparer.Ordinal)
                {
                    ["resume.payload"] = request.ResumePayload.Value
                };
                mergedState = stateWithPayload;
            }
            var mergedCheckpoint = checkpoint with { State = mergedState };

            await foreach (var evt in orchestrator.ResumeStreamAsync(mergedCheckpoint, resumePayload: null, context, cts.Token).ConfigureAwait(false))
            {
                if (evt is GraphCompleted) completed = true;
                if (evt is GraphInterrupted) interrupted = true;
                yield return evt;
            }
        }
        finally
        {
            _runs.TryRemove(request.RunId, out _);
            if (completed) entry.RecordComplete();
            else if (interrupted) entry.RecordInterrupt();
            await AuditAsync(PolicyOperation.GraphResume, handle.GraphId, handle.Version, SynthesizePrincipal(), allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask CancelAsync(AgentGraphHandle handle, string runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        if (!_graphs.TryGetValue((handle.GraphId, handle.Version), out _))
        {
            throw new GraphHandleNotFoundException(handle.GraphId, handle.Version);
        }

        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.GraphCancel, handle.GraphId, handle.Version, principal, ct).ConfigureAwait(false);

        try
        {
            if (!_runs.TryGetValue(runId, out var runEntry))
            {
                // Check checkpoint to see if it completed; if no checkpoint at all → not-found
                var checkpoint = await _checkpointer.LoadAsync(runId, ct).ConfigureAwait(false);
                if (checkpoint is null)
                {
                    throw new GraphRunNotFoundException(handle.GraphId, runId);
                }
                if (checkpoint.IsComplete)
                {
                    throw new GraphAlreadyCompleteException(runId);
                }
                return; // already not in-flight but not complete — no-op
            }
            runEntry.Cancel();
        }
        finally
        {
            await AuditAsync(PolicyOperation.GraphCancel, handle.GraphId, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask EvictAsync(AgentGraphHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.GraphEvict, handle.GraphId, handle.Version, principal, ct).ConfigureAwait(false);

        try
        {
            if (!_graphs.TryRemove((handle.GraphId, handle.Version), out var entry))
            {
                throw new GraphHandleNotFoundException(handle.GraphId, handle.Version);
            }
            entry.CancelAllRuns(_runs);
            RemoveManifest(handle.GraphId, handle.Version);
        }
        finally
        {
            await AuditAsync(PolicyOperation.GraphEvict, handle.GraphId, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async ValueTask<(AgentGraphManifest Manifest, GraphEntry Entry, string RunId)> PrepareInvocationAsync(
        AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct)
    {
        var manifest = await _graphRegistry.GetAsync(handle.GraphId, handle.Version, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            throw new GraphHandleNotFoundException(handle.GraphId, handle.Version);
        }
        if (!_graphs.TryGetValue((handle.GraphId, handle.Version), out var entry))
        {
            throw new GraphHandleNotFoundException(handle.GraphId, handle.Version);
        }
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.GraphInvoke, handle.GraphId, handle.Version, principal, ct).ConfigureAwait(false);
        var runId = string.IsNullOrWhiteSpace(request.RunId)
            ? Guid.NewGuid().ToString("N")
            : request.RunId!;
        if (_runs.ContainsKey(runId))
        {
            throw new GraphRunConflictException(handle.GraphId, runId);
        }
        return (manifest, entry, runId);
    }

    private async ValueTask<(AgentGraphManifest Manifest, GraphEntry Entry, GraphCheckpoint Checkpoint)> PrepareResumeAsync(
        AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct)
    {
        var manifest = await _graphRegistry.GetAsync(handle.GraphId, handle.Version, ct).ConfigureAwait(false);
        if (manifest is null)
        {
            throw new GraphHandleNotFoundException(handle.GraphId, handle.Version);
        }
        if (!_graphs.TryGetValue((handle.GraphId, handle.Version), out var entry))
        {
            throw new GraphHandleNotFoundException(handle.GraphId, handle.Version);
        }

        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.GraphResume, handle.GraphId, handle.Version, principal, ct).ConfigureAwait(false);

        var checkpoint = await _checkpointer.LoadAsync(request.RunId, ct).ConfigureAwait(false);
        if (checkpoint is null)
        {
            throw new GraphRunNotFoundException(handle.GraphId, request.RunId);
        }
        if (checkpoint.IsComplete)
        {
            throw new GraphAlreadyCompleteException(request.RunId);
        }
        if (!string.Equals(checkpoint.PendingInterruptId, request.InterruptId, StringComparison.Ordinal))
        {
            throw new GraphInterruptMismatchException(
                request.RunId,
                request.InterruptId,
                checkpoint.PendingInterruptId ?? "(none)");
        }
        return (manifest, entry, checkpoint);
    }

    private InProcessGraphOrchestrator<IDictionary<string, JsonElement>> BuildOrchestrator(
        AgentGraphManifest manifest, string runId, int? maxStepsOverride)
    {
        var effectiveManifest = maxStepsOverride.HasValue
            ? manifest with { MaxSteps = maxStepsOverride.Value }
            : manifest;

        return new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
            effectiveManifest,
            _agentRegistry,
            _agentLifecycle,
            _checkpointer,
            runIdFactory: () => runId);
    }

    private static async ValueTask<GraphInvocationResult> DrainInvokeAsync(
        InProcessGraphOrchestrator<IDictionary<string, JsonElement>> orchestrator,
        IDictionary<string, JsonElement> initialState,
        AgentContext context,
        string runId,
        CancellationToken ct)
    {
        IDictionary<string, JsonElement> finalState = initialState;
        string? pendingInterruptId = null;
        string? pendingInterruptNodeId = null;
        string? pendingInterruptReason = null;
        bool isComplete = false;

        await foreach (var evt in orchestrator.StreamAsync(initialState, context, ct).ConfigureAwait(false))
        {
            if (evt is GraphCompleted)
            {
                isComplete = true;
                finalState = initialState;
            }
            else if (evt is GraphInterrupted gi)
            {
                pendingInterruptId = gi.InterruptId;
                pendingInterruptNodeId = gi.NodeId;
                pendingInterruptReason = gi.Reason;
                finalState = initialState;
            }
        }

        return new GraphInvocationResult(
            RunId: runId,
            FinalState: finalState,
            IsComplete: isComplete,
            PendingInterruptId: pendingInterruptId,
            PendingInterruptNodeId: pendingInterruptNodeId,
            PendingInterruptReason: pendingInterruptReason);
    }

    private static async ValueTask<GraphInvocationResult> DrainResumeAsync(
        InProcessGraphOrchestrator<IDictionary<string, JsonElement>> orchestrator,
        GraphCheckpoint checkpoint,
        JsonElement? resumePayload,
        AgentContext context,
        string runId,
        CancellationToken ct)
    {
        // Pre-merge the caller's resume payload into state so the orchestrator picks
        // it up under the well-known "resume.payload" key. We then pass null for the
        // orchestrator's resumePayload parameter to avoid a double-write.
        IReadOnlyDictionary<string, JsonElement> mergedState = checkpoint.State;
        if (resumePayload.HasValue)
        {
            var stateWithPayload = new Dictionary<string, JsonElement>(checkpoint.State, StringComparer.Ordinal)
            {
                ["resume.payload"] = resumePayload.Value
            };
            mergedState = stateWithPayload;
        }
        var mergedCheckpoint = checkpoint with { State = mergedState };

        IDictionary<string, JsonElement> finalState = new Dictionary<string, JsonElement>(mergedState, StringComparer.Ordinal);
        string? pendingInterruptId = null;
        string? pendingInterruptNodeId = null;
        string? pendingInterruptReason = null;
        bool isComplete = false;

        await foreach (var evt in orchestrator.ResumeStreamAsync(mergedCheckpoint, resumePayload: null, context, ct).ConfigureAwait(false))
        {
            if (evt is GraphCompleted)
            {
                isComplete = true;
            }
            else if (evt is GraphInterrupted gi)
            {
                pendingInterruptId = gi.InterruptId;
                pendingInterruptNodeId = gi.NodeId;
                pendingInterruptReason = gi.Reason;
            }
        }

        return new GraphInvocationResult(
            RunId: runId,
            FinalState: finalState,
            IsComplete: isComplete,
            PendingInterruptId: pendingInterruptId,
            PendingInterruptNodeId: pendingInterruptNodeId,
            PendingInterruptReason: pendingInterruptReason);
    }

    private AgentContext BuildContext(IReadOnlyDictionary<string, string>? metadata)
    {
        // AgentContext doesn't carry a generic Properties bag — metadata from
        // GraphInvocationRequest is available to graph nodes via the state bag
        // or injected at the caller side. Return the ambient context as-is.
        return _contextAccessor.Current;
    }

    private async ValueTask GateAsync(PolicyOperation op, string graphId, string version, AgentPrincipal? principal, CancellationToken ct)
    {
        // Build a minimal null manifest for the policy call — graph manifests are
        // resolved separately; passing null signals "no manifest context available".
        var decision = await _policy.EvaluateAsync(op, manifest: null, principal, ct).ConfigureAwait(false);
        if (decision.IsAllowed) return;
        var reason = decision.Reason ?? "policy denied";
        await AuditAsync(op, graphId, version, principal, allowed: false, denyReason: reason, errorType: null).ConfigureAwait(false);
        throw new AgentPolicyDeniedException(op, reason);
    }

    private async ValueTask AuditAsync(
        PolicyOperation op,
        string? graphId,
        string? version,
        AgentPrincipal? principal,
        bool allowed,
        string? denyReason,
        string? errorType)
    {
        var entry = new AuditLogEntry(
            At: DateTimeOffset.UtcNow,
            Operation: op,
            AgentId: graphId,
            AgentVersion: version,
            PrincipalId: principal?.Id ?? "anonymous",
            TenantId: principal?.TenantId,
            Allowed: allowed,
            DenyReason: denyReason,
            ErrorType: errorType);
        try
        {
            await _audit.AppendAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit log {AuditType} threw; swallowed.", _audit.GetType().Name);
        }
    }

    private AgentPrincipal? SynthesizePrincipal()
    {
        var ctx = _contextAccessor.Current;
        if (ctx.UserId is { Length: > 0 } userId)
        {
            return new AgentPrincipal(userId, ctx.TenantId, Scopes: null);
        }
        return null;
    }

    private void RegisterManifest(AgentGraphManifest manifest)
    {
        var method = _graphRegistry.GetType().GetMethod("Register", new[] { typeof(AgentGraphManifest) });
        if (method is null)
        {
            throw new NotSupportedException(
                $"{_graphRegistry.GetType().Name} does not expose a public Register(AgentGraphManifest) method. " +
                "Subclass AgentGraphLifecycleManager and override RegisterManifest, or supply an IAgentGraphRegistry that does.");
        }
        method.Invoke(_graphRegistry, new object[] { manifest });
    }

    private void RemoveManifest(string graphId, string version)
    {
        var method = _graphRegistry.GetType().GetMethod("Remove", new[] { typeof(string), typeof(string) });
        method?.Invoke(_graphRegistry, new object[] { graphId, version });
    }

    // ── Inner types ────────────────────────────────────────────────────────────

    private sealed class GraphEntry
    {
        private int _active;
        private int _completed;
        private int _pendingInterrupt;
        private DateTimeOffset? _lastInvokedAt;
        private readonly object _gate = new();

        public void RecordInvoke()
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _active);
                _lastInvokedAt = DateTimeOffset.UtcNow;
            }
        }

        public void RecordComplete()
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
        }

        public void RecordInterrupt()
        {
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _pendingInterrupt);
        }

        public void RecordResumeFromInterrupt()
        {
            Interlocked.Decrement(ref _pendingInterrupt);
            Interlocked.Increment(ref _active);
        }

        public void RecordError()
        {
            Interlocked.Decrement(ref _active);
        }

        public void CancelAllRuns(ConcurrentDictionary<string, RunEntry> runs)
        {
            foreach (var run in runs.Values)
            {
                try { run.Cancel(); } catch { /* ignore */ }
            }
        }

        public AgentGraphStatus ToStatus(string graphId, string version)
        {
            lock (_gate)
            {
                return new AgentGraphStatus(
                    GraphId: graphId,
                    Version: version,
                    ActiveRunCount: Math.Max(0, _active),
                    CompletedRunCount: _completed,
                    PendingInterruptCount: Math.Max(0, _pendingInterrupt),
                    LastInvokedAt: _lastInvokedAt);
            }
        }
    }

    private sealed class RunEntry(string graphId, CancellationTokenSource cts)
    {
        public string GraphId { get; } = graphId;

        public void Cancel() => cts.Cancel();
    }

    /// <summary>Minimal context accessor fallback returning <see cref="AgentContext.Empty"/>.</summary>
    private sealed class AsyncLocalAgentContextAccessorFallback : IAgentContextAccessor
    {
        public AgentContext Current => AgentContext.Empty;
    }
}
