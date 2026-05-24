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
    private readonly IAgentRemoteInvoker? _remoteInvoker;
    private readonly IA2AGraphNodeInvoker? _a2aInvoker;
    private readonly Func<string?>? _bearerTokenProvider;
    private readonly IAgentGraphEventBus? _graphEventBus;
    private readonly Func<AgentGraphManifest, string, IAgentGraph<IDictionary<string, JsonElement>>>? _orchestratorFactory;

    // Per-graph counters, manifest-keyed (advisory; in-process — see class remarks).
    private readonly ConcurrentDictionary<(string Id, string Version), GraphEntry> _graphs = new();

    // Cross-host run registry: conflict detection, cancel signalling, status. In-process by
    // default; the runtime swaps in a grain-backed coordinator so cancel/status work cluster-wide.
    private readonly IGraphRunCoordinator _coordinator;

    // Cancellation tokens for runs executing on THIS host, keyed by run id. The coordinator carries
    // the durable cancel flag cluster-wide; this map turns an observed cancel into an actual stop of
    // the local orchestrator.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _localCts = new(StringComparer.Ordinal);

    // How often the executing host polls the coordinator for a cancel requested on another host.
    private static readonly TimeSpan _cancelPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Shared error code consumers can match on to translate to HTTP 404 etc.</summary>
    public const string UnknownGraphErrorType = "GraphHandleNotFound";

    /// <summary>Construct a manager. Graph-registry, agent-registry, agent-lifecycle, and checkpointer are required.</summary>
    /// <param name="graphRegistry">Registry holding graph manifests.</param>
    /// <param name="agentRegistry">Registry for resolving local agent manifests.</param>
    /// <param name="agentLifecycle">Lifecycle manager for invoking local agents.</param>
    /// <param name="checkpointer">Checkpoint store for resumable graph runs.</param>
    /// <param name="policy">Policy engine. Null uses allow-all.</param>
    /// <param name="audit">Audit log. Null is a no-op.</param>
    /// <param name="contextAccessor">Agent context accessor. Null uses async-local fallback.</param>
    /// <param name="logger">Logger. Null uses null-logger.</param>
    /// <param name="remoteInvoker">Invoker for cross-runtime graph nodes. Required when any graph manifest contains nodes with a <see cref="GraphAgentRef.RuntimeUrl"/>.</param>
    /// <param name="a2aInvoker">Invoker for A2A protocol graph nodes. Required when any graph manifest contains nodes with <see cref="GraphAgentRef.A2AUrl"/>.</param>
    /// <param name="bearerTokenProvider">Factory invoked per graph run to obtain the current bearer token for remote runtime calls. Typically reads from <c>IHttpContextAccessor</c>.</param>
    /// <param name="graphEventBus">Event bus to fan out graph lifecycle events (started, node completed, etc.). Null ⇒ events are dropped.</param>
    /// <param name="orchestratorFactory">
    /// Optional factory that overrides the default <see cref="InProcessGraphOrchestrator{TState}"/> creation.
    /// Receives the effective manifest (maxSteps already applied) and the run id; returns the orchestrator to use.
    /// Use this to wire in <c>MafGraphOrchestrator</c> for graphs that require concurrent-edge support.
    /// </param>
    /// <param name="coordinator">Run registry for conflict detection, cancellation signalling, and status. Null uses an in-process coordinator (single-host only).</param>
    public AgentGraphLifecycleManager(
        IAgentGraphRegistry graphRegistry,
        IAgentRegistry agentRegistry,
        IAgentLifecycleManager agentLifecycle,
        IGraphCheckpointer checkpointer,
        IAgentPolicyEngine? policy = null,
        IAuditLog? audit = null,
        IAgentContextAccessor? contextAccessor = null,
        ILogger<AgentGraphLifecycleManager>? logger = null,
        IAgentRemoteInvoker? remoteInvoker = null,
        IA2AGraphNodeInvoker? a2aInvoker = null,
        Func<string?>? bearerTokenProvider = null,
        IAgentGraphEventBus? graphEventBus = null,
        Func<AgentGraphManifest, string, IAgentGraph<IDictionary<string, JsonElement>>>? orchestratorFactory = null,
        IGraphRunCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(graphRegistry);
        ArgumentNullException.ThrowIfNull(agentRegistry);
        ArgumentNullException.ThrowIfNull(agentLifecycle);
        ArgumentNullException.ThrowIfNull(checkpointer);
        _coordinator = coordinator ?? new InProcessGraphRunCoordinator();
        _graphRegistry = graphRegistry;
        _agentRegistry = agentRegistry;
        _agentLifecycle = agentLifecycle;
        _checkpointer = checkpointer;
        _policy = policy ?? NullAgentPolicyEngine.Instance;
        _audit = audit ?? NullAuditLog.Instance;
        _contextAccessor = contextAccessor ?? new AsyncLocalAgentContextAccessorFallback();
        _logger = logger ?? NullLogger<AgentGraphLifecycleManager>.Instance;
        _remoteInvoker = remoteInvoker;
        _a2aInvoker = a2aInvoker;
        _bearerTokenProvider = bearerTokenProvider;
        _graphEventBus = graphEventBus;
        _orchestratorFactory = orchestratorFactory;
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
        if (!await _coordinator.TryStartAsync(runId, handle.GraphId, handle.Version, ct).ConfigureAwait(false))
        {
            throw new GraphRunConflictException(handle.GraphId, runId);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _localCts[runId] = cts;
        _ = WatchForCancellationAsync(runId, cts);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? errorType = null;
        var outcome = GraphRunOutcome.Failed;
        try
        {
            entry.RecordInvoke();
            var context = BuildContext(request.Metadata);
            var orchestrator = BuildOrchestrator(manifest, runId, request.MaxSteps);
            GraphInvocationResult result = await DrainInvokeAsync(orchestrator, request.InitialState, context, runId, cts.Token).ConfigureAwait(false);

            if (result.IsComplete)
            {
                entry.RecordComplete();
                outcome = GraphRunOutcome.Completed;
            }
            else
            {
                entry.RecordInterrupt();
                outcome = GraphRunOutcome.Interrupted;
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
            _localCts.TryRemove(runId, out _);
            await _coordinator.CompleteAsync(runId, outcome, CancellationToken.None).ConfigureAwait(false);
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
        if (!await _coordinator.TryStartAsync(runId, handle.GraphId, handle.Version, ct).ConfigureAwait(false))
        {
            throw new GraphRunConflictException(handle.GraphId, runId);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _localCts[runId] = cts;
        _ = WatchForCancellationAsync(runId, cts);

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
            _localCts.TryRemove(runId, out _);
            await _coordinator.CompleteAsync(runId,
                completed ? GraphRunOutcome.Completed : interrupted ? GraphRunOutcome.Interrupted : GraphRunOutcome.Failed,
                CancellationToken.None).ConfigureAwait(false);
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
        await _coordinator.MarkActiveAsync(request.RunId, handle.GraphId, handle.Version, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _localCts[request.RunId] = cts;
        _ = WatchForCancellationAsync(request.RunId, cts);

        string? errorType = null;
        var outcome = GraphRunOutcome.Failed;
        try
        {
            entry.RecordResumeFromInterrupt();
            var context = BuildContext(request.Metadata);
            var orchestrator = BuildOrchestrator(manifest, request.RunId, maxStepsOverride: null);
            var payload = request.ResumePayload;
            GraphInvocationResult result = await DrainResumeAsync(orchestrator, checkpoint, payload, context, request.RunId, cts.Token).ConfigureAwait(false);

            if (result.IsComplete) { entry.RecordComplete(); outcome = GraphRunOutcome.Completed; }
            else { entry.RecordInterrupt(); outcome = GraphRunOutcome.Interrupted; }
            return result;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            _localCts.TryRemove(request.RunId, out _);
            await _coordinator.CompleteAsync(request.RunId, outcome, CancellationToken.None).ConfigureAwait(false);
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
        await _coordinator.MarkActiveAsync(request.RunId, handle.GraphId, handle.Version, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _localCts[request.RunId] = cts;
        _ = WatchForCancellationAsync(request.RunId, cts);

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
            var resumable = (IResumableAgentGraph<IDictionary<string, JsonElement>>)orchestrator;

            await foreach (var evt in resumable.ResumeStreamAsync(mergedCheckpoint, resumePayload: null, context, cts.Token).ConfigureAwait(false))
            {
                if (evt is GraphCompleted) completed = true;
                if (evt is GraphInterrupted) interrupted = true;
                yield return evt;
            }
        }
        finally
        {
            _localCts.TryRemove(request.RunId, out _);
            await _coordinator.CompleteAsync(request.RunId,
                completed ? GraphRunOutcome.Completed : interrupted ? GraphRunOutcome.Interrupted : GraphRunOutcome.Failed,
                CancellationToken.None).ConfigureAwait(false);
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
            // Signal cancellation cluster-wide (durable; reachable from any host).
            await _coordinator.RequestCancelAsync(runId, ct).ConfigureAwait(false);

            // Fast path: the run executes on this host — stop its orchestrator immediately.
            if (_localCts.TryGetValue(runId, out var cts))
            {
                cts.Cancel();
                return;
            }

            // Not on this host. If the coordinator still tracks it, the owning host will observe
            // the cancel flag and stop cooperatively. Otherwise fall back to the checkpoint to
            // distinguish already-complete / no-op / not-found.
            if (await _coordinator.GetAsync(runId, ct).ConfigureAwait(false) is not null)
            {
                return;
            }

            var checkpoint = await _checkpointer.LoadAsync(runId, ct).ConfigureAwait(false);
            if (checkpoint is null)
            {
                throw new GraphRunNotFoundException(handle.GraphId, runId);
            }
            if (checkpoint.IsComplete)
            {
                throw new GraphAlreadyCompleteException(runId);
            }
            // not in-flight but not complete — no-op
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
            if (!_graphs.TryRemove((handle.GraphId, handle.Version), out _))
            {
                throw new GraphHandleNotFoundException(handle.GraphId, handle.Version);
            }
            // Stop any runs executing on this host. (Cross-host runs stop cooperatively when the
            // owning host next checks the coordinator's cancel flag.)
            foreach (var cts in _localCts.Values)
            {
                try { cts.Cancel(); } catch { /* best-effort */ }
            }
            RemoveManifest(handle.GraphId, handle.Version);
        }
        finally
        {
            await AuditAsync(PolicyOperation.GraphEvict, handle.GraphId, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Background poll: while a run executes on this host, watch the coordinator for a cancel
    /// requested elsewhere (another silo) and cancel the local token when observed. Same-host
    /// cancels short-circuit via <see cref="CancelAsync"/> cancelling the token directly, which
    /// also ends this loop (the linked token trips). Self-cleaning — exits when the run ends.
    /// </summary>
    private async Task WatchForCancellationAsync(string runId, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(_cancelPollInterval, cts.Token).ConfigureAwait(false);
                if (await _coordinator.IsCancelRequestedAsync(runId, cts.Token).ConfigureAwait(false))
                {
                    cts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Run ended (token disposed/cancelled) — nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancel watcher for run {RunId} ended with an error.", runId);
        }
    }

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
        // Authoritative conflict detection happens at IGraphRunCoordinator.TryStartAsync (atomic,
        // cluster-wide). No pre-check here.
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

    private IAgentGraph<IDictionary<string, JsonElement>> BuildOrchestrator(
        AgentGraphManifest manifest, string runId, int? maxStepsOverride)
    {
        var effectiveManifest = maxStepsOverride.HasValue
            ? manifest with { MaxSteps = maxStepsOverride.Value }
            : manifest;

        if (_orchestratorFactory is not null)
            return _orchestratorFactory(effectiveManifest, runId);

        if (effectiveManifest.Edges.Any(e => e.Concurrent))
            throw new InvalidOperationException(
                $"Graph '{effectiveManifest.Id}' declares concurrent (fan-out/fan-in) edges, but no " +
                "orchestratorFactory is configured. InProcessGraphOrchestrator is sequential-only and " +
                "would mis-execute concurrent branches (only the first matching edge is followed). Wire " +
                "MafGraphOrchestrator via the orchestratorFactory parameter to enable concurrent execution.");

        return new InProcessGraphOrchestrator<IDictionary<string, JsonElement>>(
            effectiveManifest,
            _agentRegistry,
            _agentLifecycle,
            _checkpointer,
            runIdFactory: () => runId,
            remoteInvoker: _remoteInvoker,
            a2aInvoker: _a2aInvoker,
            bearerToken: _bearerTokenProvider?.Invoke(),
            graphEventBus: _graphEventBus,
            logger: _logger);
    }

    private static async ValueTask<GraphInvocationResult> DrainInvokeAsync(
        IAgentGraph<IDictionary<string, JsonElement>> orchestrator,
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
            if (evt is GraphCompleted gc)
            {
                isComplete = true;
                if (gc.FinalState is not null)
                    finalState = new Dictionary<string, JsonElement>(gc.FinalState);
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

    private static async ValueTask<GraphInvocationResult> DrainResumeAsync(
        IAgentGraph<IDictionary<string, JsonElement>> orchestrator,
        GraphCheckpoint checkpoint,
        JsonElement? resumePayload,
        AgentContext context,
        string runId,
        CancellationToken ct)
    {
        var resumable = (IResumableAgentGraph<IDictionary<string, JsonElement>>)orchestrator;

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

        await foreach (var evt in resumable.ResumeStreamAsync(mergedCheckpoint, resumePayload: null, context, ct).ConfigureAwait(false))
        {
            if (evt is GraphCompleted gc)
            {
                isComplete = true;
                if (gc.FinalState is not null)
                    finalState = new Dictionary<string, JsonElement>(gc.FinalState);
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
            return new AgentPrincipal(userId, ctx.TenantId, ctx.Scopes);
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

    /// <summary>Minimal context accessor fallback returning <see cref="AgentContext.Empty"/>.</summary>
    private sealed class AsyncLocalAgentContextAccessorFallback : IAgentContextAccessor
    {
        public AgentContext Current => AgentContext.Empty;
    }
}
