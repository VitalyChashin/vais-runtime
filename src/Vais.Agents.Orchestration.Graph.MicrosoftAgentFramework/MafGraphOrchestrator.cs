// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Vais.Agents.Core;

namespace Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

/// <summary>
/// Microsoft Agent Framework Workflows adapter for <see cref="IAgentGraph{TState}"/>.
/// Projects an <see cref="AgentGraphManifest"/> onto a MAF <see cref="Workflow"/> so
/// consumers on the MAF stack can run graphs through the same contract as the
/// in-process orchestrator, with MAF-native event streaming + checkpointing hooks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Execution shape.</b> Each manifest node becomes one MAF executor binding;
/// edges are declared structurally via <c>AddEdge(from, to)</c>, and the node
/// executor itself evaluates outgoing edges (same async <see cref="GraphEdgePredicate"/>
/// evaluator as the in-process orchestrator) and sends the next <see cref="GraphMessage"/>
/// to the selected target via <c>IWorkflowContext.SendMessageAsync(msg, targetId)</c>.
/// This keeps the semantics identical to <c>InProcessGraphOrchestrator</c> while still
/// riding MAF's executor fan-out + streaming-event infrastructure.
/// </para>
/// <para>
/// <b>Durable resume.</b> When an <see cref="IGraphCheckpointer"/> is supplied the
/// orchestrator implements <see cref="IResumableAgentGraph{TState}"/>: interrupt nodes
/// save a checkpoint before halting; <see cref="ResumeAsync"/> / <see cref="ResumeStreamAsync"/>
/// reload that state and rebuild the MAF workflow starting at the interrupt node, which
/// skips its body via the <see cref="GraphMessage.ResumeFromNodeId"/> flag and evaluates
/// outgoing edges — identical semantics to <c>InProcessGraphOrchestrator.ResumeAsync</c>.
/// </para>
/// <para>
/// <b>Fan-out / fan-in.</b> Manifest edges with <c>concurrent: true</c> are projected
/// onto MAF's <c>AddFanOutEdge</c> / <c>AddFanInBarrierEdge</c> topology by
/// <see cref="MafGraphBuilder.Build"/>. Fork executors yield without routing;
/// MAF infrastructure dispatches to all branch targets. Join nodes use
/// <c>GraphJoinNodeExecutor</c> to accumulate branch state before executing the node body.
/// <see cref="InProcessGraphOrchestrator"/> does not support fan-out — graphs with
/// concurrent edges must use this orchestrator.
/// </para>
/// <para>
/// <b>Notes:</b> MAF-native conditional edges (<c>AddEdge&lt;T&gt;(source, target, condition)</c>)
/// are unused; all routing happens inside the executor. RequestPort-based HITL is not
/// used; interrupt nodes halt via <c>IWorkflowContext.RequestHaltAsync</c> and emit a
/// <see cref="GraphInterrupted"/> event instead.
/// </para>
/// <para>
/// Consumers who want MAF's richer Workflow features (sub-workflows, typed
/// source-generated handlers, native conditional edges) can call
/// <see cref="MafGraphBuilder.Build"/> directly to get the <see cref="Workflow"/>
/// and use <c>InProcessExecution</c> themselves — the adapter is thin by design.
/// </para>
/// </remarks>
public class MafGraphOrchestrator<TState> : IAgentGraph<TState>, IResumableAgentGraph<TState>, IHitlAgentGraph<TState>
{
    private readonly AgentGraphManifest _manifest;
    private readonly IAgentRegistry _registry;
    private readonly IAgentLifecycleManager _lifecycle;
    private readonly Func<GraphHandlerRef, IGraphEdgePredicate>? _predicateResolver;
    private readonly Func<GraphHandlerRef, IGraphEdgeEffect>? _effectResolver;
    private readonly Func<GraphHandlerRef, IGraphCodeNode>? _codeNodeResolver;
    private readonly Func<GraphHandlerRef, IGraphStateReducer>? _reducerResolver;
    private readonly Func<string>? _runIdFactory;
    private readonly IGraphCheckpointer? _checkpointer;
    private readonly IAgentGraphEventBus _graphEventBus;
    private readonly IAgentRemoteInvoker? _remoteInvoker;
    private readonly IA2AGraphNodeInvoker? _a2aInvoker;
    private readonly string? _bearerToken;
    private readonly IGraphExpressionEvaluator? _expressionEvaluator;

    private static readonly ActivitySource _activitySource = new("Vais.Agents.Core.Graph", "1.0.0");

    /// <summary>Default max-step ceiling matching the in-process orchestrator.</summary>
    public const int DefaultMaxSteps = 1000;

    /// <summary>Construct the MAF-backed orchestrator.</summary>
    /// <param name="manifest">Graph manifest to run.</param>
    /// <param name="registry">Agent registry for resolving agent-kind nodes.</param>
    /// <param name="lifecycle">Lifecycle manager for invoking resolved agents.</param>
    /// <param name="predicateResolver">Resolver for <see cref="GraphHandlerRef"/> edge predicates. Null means handler-ref predicates throw.</param>
    /// <param name="effectResolver">Resolver for <see cref="GraphHandlerRef"/> edge effects.</param>
    /// <param name="codeNodeResolver">Resolver for Code-kind nodes. Null means code-kind nodes throw.</param>
    /// <param name="reducerResolver">Resolver for <see cref="GraphStateReducer.HandlerRef"/> reducer declarations in <see cref="AgentGraphManifest.StateReducers"/>. Null means handler-ref reducers throw.</param>
    /// <param name="runIdFactory">Factory for run ids stamped on events and checkpoints. Null uses <c>Guid.NewGuid().ToString("N")</c>.</param>
    /// <param name="checkpointer">
    /// Checkpoint store used by <see cref="ResumeAsync"/> / <see cref="ResumeStreamAsync"/>.
    /// Pass an <c>InMemoryCheckpointer</c> for tests. Null skips all checkpoint saves
    /// (compatible with v0.9 callers that do not need durable resume).
    /// </param>
    /// <param name="graphEventBus">Bus to fan out graph lifecycle events to. Null uses <see cref="NullAgentGraphEventBus"/>.</param>
    /// <param name="remoteInvoker">Invoker for cross-runtime agent nodes (<see cref="GraphAgentRef.RuntimeUrl"/>). Null means runtime-url nodes throw at runtime.</param>
    /// <param name="a2aInvoker">Invoker for A2A protocol agent nodes (<see cref="GraphAgentRef.A2AUrl"/>). Null means A2A-url nodes throw at runtime.</param>
    /// <param name="bearerToken">Bearer token forwarded to remote runtimes for identity propagation.</param>
    /// <param name="expressionEvaluator">Evaluator for <see cref="GraphEdgePredicate.Expression"/> predicates. Null means expression predicates throw. Register via <c>AddPowerFxExpressionEvaluator()</c>.</param>
    public MafGraphOrchestrator(
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver = null,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver = null,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver = null,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver = null,
        Func<string>? runIdFactory = null,
        IGraphCheckpointer? checkpointer = null,
        IAgentGraphEventBus? graphEventBus = null,
        IAgentRemoteInvoker? remoteInvoker = null,
        IA2AGraphNodeInvoker? a2aInvoker = null,
        string? bearerToken = null,
        IGraphExpressionEvaluator? expressionEvaluator = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifecycle);
        _manifest = manifest;
        _registry = registry;
        _lifecycle = lifecycle;
        _predicateResolver = predicateResolver;
        _effectResolver = effectResolver;
        _codeNodeResolver = codeNodeResolver;
        _reducerResolver = reducerResolver;
        _runIdFactory = runIdFactory;
        _checkpointer = checkpointer;
        _graphEventBus = graphEventBus ?? NullAgentGraphEventBus.Instance;
        _remoteInvoker = remoteInvoker;
        _a2aInvoker = a2aInvoker;
        _bearerToken = bearerToken;
        _expressionEvaluator = expressionEvaluator;
    }

    /// <inheritdoc />
    public async ValueTask<TState> InvokeAsync(TState initial, AgentContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        IDictionary<string, JsonElement> bag = StateBagConverter.ToBag(initial);
        await foreach (var _ in RunAsync(bag, context, resumeFromNodeId: null, resumedRunId: null, cancellationToken).ConfigureAwait(false))
        {
            // Drain the event stream; state is mutated in-place on the passed `bag`.
        }
        return StateBagConverter.FromBag<TState>(bag);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AgentGraphEvent> StreamAsync(TState initial, AgentContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bag = StateBagConverter.ToBag(initial);
        return RunAsync(bag, context, resumeFromNodeId: null, resumedRunId: null, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<TState> ResumeAsync(
        GraphCheckpoint checkpoint,
        TState? resumePayload,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(context);
        if (_checkpointer is null)
        {
            throw new InvalidOperationException(
                "Cannot resume a MafGraphOrchestrator that was constructed without a checkpointer. " +
                "Pass an IGraphCheckpointer to the constructor to enable durable resume.");
        }
        if (checkpoint.NextNodeId is null)
        {
            throw new InvalidOperationException(
                $"Cannot resume from checkpoint '{checkpoint.RunId}' — no NextNodeId (was the graph already completed?).");
        }
        if (!string.Equals(checkpoint.GraphId, _manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Checkpoint belongs to graph '{checkpoint.GraphId}'; this orchestrator hosts '{_manifest.Id}'.");
        }

        var bag = new Dictionary<string, JsonElement>(checkpoint.State, StringComparer.Ordinal);
        if (resumePayload is not null)
        {
            bag["resume.payload"] = JsonSerializer.SerializeToElement(resumePayload);
        }

        await foreach (var _ in RunAsync(bag, context, checkpoint.NextNodeId, checkpoint.RunId, cancellationToken).ConfigureAwait(false))
        {
            // Drain.
        }

        return StateBagConverter.FromBag<TState>(bag);
    }

    /// <summary>
    /// Stream variant of <see cref="ResumeAsync"/> — yields the full <see cref="AgentGraphEvent"/>
    /// taxonomy starting with <see cref="GraphResumed"/>.
    /// </summary>
    public IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(
        GraphCheckpoint checkpoint,
        TState? resumePayload,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(context);
        if (_checkpointer is null)
        {
            throw new InvalidOperationException(
                "Cannot resume a MafGraphOrchestrator that was constructed without a checkpointer.");
        }
        if (checkpoint.NextNodeId is null)
        {
            throw new InvalidOperationException(
                $"Cannot resume from checkpoint '{checkpoint.RunId}' — no NextNodeId.");
        }
        var bag = new Dictionary<string, JsonElement>(checkpoint.State, StringComparer.Ordinal);
        if (resumePayload is not null)
        {
            bag["resume.payload"] = JsonSerializer.SerializeToElement(resumePayload);
        }
        return RunAsync(bag, context, checkpoint.NextNodeId, checkpoint.RunId, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AgentGraphEvent> StreamWithHitlAsync(
        TState initial,
        AgentContext context,
        Func<GraphInterrupted, CancellationToken, ValueTask<TState?>> handleInterrupt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handleInterrupt);
        var bag = StateBagConverter.ToBag(initial);
        return RunWithHitlAsync(bag, context, handleInterrupt, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<TState> InvokeWithHitlAsync(
        TState initial,
        AgentContext context,
        Func<GraphInterrupted, CancellationToken, ValueTask<TState?>> handleInterrupt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handleInterrupt);
        IDictionary<string, JsonElement> bag = StateBagConverter.ToBag(initial);
        await foreach (var _ in RunWithHitlAsync(bag, context, handleInterrupt, cancellationToken).ConfigureAwait(false))
        {
        }
        return StateBagConverter.FromBag<TState>(bag);
    }

    private async IAsyncEnumerable<AgentGraphEvent> RunWithHitlAsync(
        IDictionary<string, JsonElement> state,
        AgentContext context,
        Func<GraphInterrupted, CancellationToken, ValueTask<TState?>> handleInterrupt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runId = _runIdFactory?.Invoke() ?? Guid.NewGuid().ToString("N");
        var maxSteps = _manifest.MaxSteps ?? DefaultMaxSteps;

        var buildResult = MafGraphBuilder.BuildForHitl(
            _manifest, _registry, _lifecycle,
            _predicateResolver, _effectResolver, _codeNodeResolver,
            reducerResolver: _reducerResolver,
            remoteInvoker: _remoteInvoker,
            a2aInvoker: _a2aInvoker,
            bearerToken: _bearerToken,
            expressionEvaluator: _expressionEvaluator,
            checkpointer: _checkpointer);
        var workflow = buildResult.Workflow;
        var portIdToNodeId = buildResult.PortIdToNodeId;

        var watch = Stopwatch.StartNew();

        var startedEvt = new GraphStarted(
            DateTimeOffset.UtcNow, context, runId, 0,
            _manifest.Id, _manifest.Version, _manifest.Entry);
        await _graphEventBus.PublishAsync(startedEvt, cancellationToken).ConfigureAwait(false);
        yield return startedEvt;

        var initialMessage = new GraphMessage(
            State: new Dictionary<string, JsonElement>(state, StringComparer.Ordinal),
            SuperStep: 0,
            RunId: runId,
            MaxSteps: maxSteps);

        // OffThread prevents deadlock: the RequestPortBinding executor blocks its superstep thread
        // while WatchStreamAsync needs to advance on the outer loop to emit RequestInfoEvent.
        await using var run = await InProcessExecution.OffThread
            .OpenStreamingAsync(workflow, sessionId: runId, cancellationToken)
            .ConfigureAwait(false);
        await run.TrySendMessageAsync(initialMessage).ConfigureAwait(false);

        GraphMessage? finalMessage = null;
        int superStep = 0;
        GraphRecursionException? recursionFailure = null;
        // Buffered GraphInterrupted from the emitter; matched to RequestInfoEvent by node-id.
        GraphInterrupted? pendingInterrupted = null;

        await foreach (var wfEvent in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            // Buffer the translated GraphInterrupted so we can pass it to the handler
            // when the matching RequestInfoEvent fires (emitter fires AddEventAsync before
            // SendMessageAsync, so GraphInterruptedEvent always precedes RequestInfoEvent).
            if (wfEvent is GraphInterruptedEvent intEvt)
            {
                pendingInterrupted = new GraphInterrupted(
                    DateTimeOffset.UtcNow, context, runId, superStep,
                    intEvt.NodeId, intEvt.InterruptId, intEvt.Reason);
                await _graphEventBus.PublishAsync(pendingInterrupted, cancellationToken).ConfigureAwait(false);
                yield return pendingInterrupted;
                continue;
            }

            // RequestPort blocked — call HITL handler inline and feed response back.
            if (wfEvent is RequestInfoEvent reqInfo &&
                portIdToNodeId.TryGetValue(reqInfo.Request.PortInfo.PortId, out var hitlNodeId))
            {
                var interruptedEvt = pendingInterrupted
                    ?? new GraphInterrupted(DateTimeOffset.UtcNow, context, runId, superStep,
                        hitlNodeId, Guid.NewGuid().ToString("N"), Reason: null);
                pendingInterrupted = null;

                var handlerResult = await handleInterrupt(interruptedEvt, cancellationToken).ConfigureAwait(false);
                if (handlerResult is null)
                {
                    await run.CancelRunAsync().ConfigureAwait(false);
                    var abortExc = new GraphHitlAbortedException(hitlNodeId);
                    var failEvt = new GraphFailed(
                        DateTimeOffset.UtcNow, context, runId, superStep,
                        nameof(GraphHitlAbortedException), abortExc.Message, watch.Elapsed);
                    await _graphEventBus.PublishAsync(failEvt, cancellationToken).ConfigureAwait(false);
                    yield return failEvt;
                    throw abortExc;
                }

                // Merge handler result under "hitl.response" and emit StateUpdated.
                reqInfo.Request.TryGetDataAs<GraphMessage>(out var blockedMsg);
                var mergedState = new Dictionary<string, JsonElement>(
                    blockedMsg?.State ?? state, StringComparer.Ordinal);
                mergedState["hitl.response"] = JsonSerializer.SerializeToElement(handlerResult);
                var stateEvt = new StateUpdated(
                    DateTimeOffset.UtcNow, context, runId, superStep,
                    new[] { "hitl.response" });
                await _graphEventBus.PublishAsync(stateEvt, cancellationToken).ConfigureAwait(false);
                yield return stateEvt;

                var responseMsg = (blockedMsg ?? initialMessage) with { State = mergedState };
                await run.SendResponseAsync(reqInfo.Request.CreateResponse(responseMsg)).ConfigureAwait(false);
                continue;
            }

            foreach (var translated in TranslateEvent(wfEvent, context, runId, ref superStep, ref finalMessage, ref recursionFailure))
            {
                await _graphEventBus.PublishAsync(translated, cancellationToken).ConfigureAwait(false);
                yield return translated;
            }
        }

        if (recursionFailure is not null)
        {
            throw recursionFailure;
        }

        if (finalMessage is not null)
        {
            state.Clear();
            foreach (var (k, v) in finalMessage.State)
            {
                state[k] = v;
            }

            var completedEvt = new GraphCompleted(
                DateTimeOffset.UtcNow, context, runId, superStep,
                finalMessage.SourceNodeId ?? _manifest.Entry, watch.Elapsed,
                FinalState: (IReadOnlyDictionary<string, JsonElement>)finalMessage.State);
            await _graphEventBus.PublishAsync(completedEvt, cancellationToken).ConfigureAwait(false);
            yield return completedEvt;
        }
    }

    private async IAsyncEnumerable<AgentGraphEvent> RunAsync(
        IDictionary<string, JsonElement> state,
        AgentContext context,
        string? resumeFromNodeId,
        string? resumedRunId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runId = resumedRunId ?? _runIdFactory?.Invoke() ?? Guid.NewGuid().ToString("N");
        var maxSteps = _manifest.MaxSteps ?? DefaultMaxSteps;
        var isResume = resumeFromNodeId is not null;

        // Root activity — mirrors InProcessGraphOrchestrator so all node spans are grouped
        // under a single Langfuse trace. Activity.Current is set to null first to force a
        // trace root rather than attaching as a child of whatever the caller has active.
        Activity.Current = null;
        using var graphActivity = _activitySource.StartActivity("graph.run", ActivityKind.Internal);
        graphActivity?.SetTag("graph.id",      _manifest.Id);
        graphActivity?.SetTag("graph.version", _manifest.Version);
        graphActivity?.SetTag("graph.run_id",  runId);
        graphActivity?.SetTag("graph.entry",   _manifest.Entry);
        graphActivity?.SetTag("langfuse.trace.name", _manifest.Id);
        if (!string.IsNullOrEmpty(context.CorrelationId))
            graphActivity?.SetTag("langfuse.session.id", context.CorrelationId);
        else if (!string.IsNullOrEmpty(context.UserId))
            graphActivity?.SetTag("langfuse.session.id", context.UserId);
        if (state.Count > 0)
            graphActivity?.SetTag("langfuse.observation.input", JsonSerializer.Serialize(state));

        // Build the MAF workflow starting at the resume node (for resume) or the manifest
        // entry (for fresh runs). Starting at the interrupt node ensures the initial message
        // is delivered directly to that executor, where it skips its body via ResumeFromNodeId.
        var workflow = MafGraphBuilder.Build(
            _manifest,
            _registry,
            _lifecycle,
            _predicateResolver,
            _effectResolver,
            _codeNodeResolver,
            reducerResolver: _reducerResolver,
            remoteInvoker: _remoteInvoker,
            a2aInvoker: _a2aInvoker,
            bearerToken: _bearerToken,
            expressionEvaluator: _expressionEvaluator,
            startNodeId: resumeFromNodeId,
            checkpointer: _checkpointer);

        var watch = Stopwatch.StartNew();

        if (isResume)
        {
            // Resume semantics: read the interrupt id from state for event correlation.
            var resumedInterruptId = state.TryGetValue("resume.interruptId", out var ii) && ii.ValueKind == JsonValueKind.String
                ? ii.GetString() ?? string.Empty
                : string.Empty;
            var resumedEvt = new GraphResumed(
                DateTimeOffset.UtcNow, context, runId, 0,
                resumeFromNodeId!, resumedInterruptId);
            await _graphEventBus.PublishAsync(resumedEvt, cancellationToken).ConfigureAwait(false);
            yield return resumedEvt;
        }
        else
        {
            var startedEvt = new GraphStarted(
                DateTimeOffset.UtcNow, context, runId, 0,
                _manifest.Id, _manifest.Version, _manifest.Entry);
            await _graphEventBus.PublishAsync(startedEvt, cancellationToken).ConfigureAwait(false);
            yield return startedEvt;
        }

        var initialMessage = new GraphMessage(
            State: new Dictionary<string, JsonElement>(state, StringComparer.Ordinal),
            SuperStep: 0,
            RunId: runId,
            MaxSteps: maxSteps)
        {
            ResumeFromNodeId = resumeFromNodeId,
        };

        // Run the workflow via MAF's InProcessExecution. The node executors send
        // GraphMessages to target node ids; terminal nodes emit via YieldOutputAsync.
        await using var run = await InProcessExecution
            .RunStreamingAsync(workflow, initialMessage, sessionId: runId, cancellationToken)
            .ConfigureAwait(false);

        GraphMessage? finalMessage = null;
        int superStep = 0;
        GraphRecursionException? recursionFailure = null;
        bool interrupted = false;

        await foreach (var wfEvent in run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            if (wfEvent is GraphInterruptedEvent)
            {
                interrupted = true;
            }
            foreach (var translated in TranslateEvent(wfEvent, context, runId, ref superStep, ref finalMessage, ref recursionFailure))
            {
                await _graphEventBus.PublishAsync(translated, cancellationToken).ConfigureAwait(false);
                yield return translated;
            }
        }

        if (recursionFailure is not null)
        {
            throw recursionFailure;
        }

        if (finalMessage is not null)
        {
            // Copy final state back to caller's bag so InvokeAsync / ResumeAsync see the result.
            state.Clear();
            foreach (var (k, v) in finalMessage.State)
            {
                state[k] = v;
            }

            // Interrupted runs already emitted GraphInterrupted — don't also mark them Completed.
            if (!interrupted)
            {
                graphActivity?.SetTag("langfuse.observation.output", JsonSerializer.Serialize(finalMessage.State));
                var completedEvt = new GraphCompleted(DateTimeOffset.UtcNow, context, runId, superStep,
                    finalMessage.SourceNodeId ?? _manifest.Entry, watch.Elapsed,
                    FinalState: (IReadOnlyDictionary<string, JsonElement>)finalMessage.State);
                await _graphEventBus.PublishAsync(completedEvt, cancellationToken).ConfigureAwait(false);
                yield return completedEvt;
            }
        }
    }

    private static IEnumerable<AgentGraphEvent> TranslateEvent(
        WorkflowEvent wfEvent,
        AgentContext context,
        string runId,
        ref int superStep,
        ref GraphMessage? finalMessage,
        ref GraphRecursionException? recursionFailure)
    {
        switch (wfEvent)
        {
            case ExecutorInvokedEvent invoked:
                // MAF emits this per super-step per executor. Our taxonomy exposes NodeStarted.
                return new AgentGraphEvent[]
                {
                    new NodeStarted(DateTimeOffset.UtcNow, context, runId, superStep,
                        invoked.ExecutorId, invoked.ExecutorId),
                };

            case ExecutorCompletedEvent completed:
                superStep++;
                return new AgentGraphEvent[]
                {
                    new NodeCompleted(DateTimeOffset.UtcNow, context, runId, superStep - 1,
                        completed.ExecutorId, completed.ExecutorId, TimeSpan.Zero),
                };

            case ExecutorFailedEvent failed:
                if (failed.Data is GraphRecursionException gre)
                {
                    recursionFailure = gre;
                }
                return new AgentGraphEvent[]
                {
                    new GraphFailed(DateTimeOffset.UtcNow, context, runId, superStep,
                        failed.Data?.GetType().Name ?? "UnknownError",
                        failed.Data?.ToString() ?? "Graph failed in MAF executor.",
                        TimeSpan.Zero),
                };

            case WorkflowOutputEvent output when output.Data is GraphMessage msg:
                finalMessage = msg;
                return Array.Empty<AgentGraphEvent>();

            case WorkflowErrorEvent error:
                return new AgentGraphEvent[]
                {
                    new GraphFailed(DateTimeOffset.UtcNow, context, runId, superStep,
                        error.Data?.GetType().Name ?? "WorkflowError",
                        error.Data?.ToString() ?? "Workflow error.",
                        TimeSpan.Zero),
                };

            case NodeAgentInvokedEvent nai:
                return new AgentGraphEvent[]
                {
                    new NodeAgentInvoked(DateTimeOffset.UtcNow, context, runId, superStep,
                        nai.NodeId, nai.AgentId, nai.InputText, nai.OutputText,
                        nai.InputTokens, nai.OutputTokens),
                };

            case GraphInterruptedEvent interrupt:
                return new AgentGraphEvent[]
                {
                    new GraphInterrupted(DateTimeOffset.UtcNow, context, runId, superStep,
                        interrupt.NodeId, interrupt.InterruptId, interrupt.Reason),
                };

            case EdgeTraversedEvent edge:
                return new AgentGraphEvent[]
                {
                    new EdgeTraversed(DateTimeOffset.UtcNow, context, runId, superStep, edge.From, edge.To),
                };

            case StateUpdatedEvent stateUpdate:
                return new AgentGraphEvent[]
                {
                    new StateUpdated(DateTimeOffset.UtcNow, context, runId, superStep, stateUpdate.ChangedKeys),
                };

            default:
                return Array.Empty<AgentGraphEvent>();
        }
    }
}

/// <summary>
/// Non-generic <see cref="MafGraphOrchestrator{TState}"/> specialisation over
/// <see cref="IDictionary{TKey,TValue}"/> of <see cref="JsonElement"/> — the shape
/// used by declarative YAML-authored graphs.
/// </summary>
public sealed class MafGraphOrchestrator : MafGraphOrchestrator<IDictionary<string, JsonElement>>, IAgentGraph
{
    /// <inheritdoc />
    public MafGraphOrchestrator(
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver = null,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver = null,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver = null,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver = null,
        Func<string>? runIdFactory = null,
        IGraphCheckpointer? checkpointer = null,
        IAgentGraphEventBus? graphEventBus = null,
        IAgentRemoteInvoker? remoteInvoker = null,
        IA2AGraphNodeInvoker? a2aInvoker = null,
        string? bearerToken = null,
        IGraphExpressionEvaluator? expressionEvaluator = null)
        : base(manifest, registry, lifecycle, predicateResolver, effectResolver, codeNodeResolver, reducerResolver, runIdFactory, checkpointer, graphEventBus, remoteInvoker, a2aInvoker, bearerToken, expressionEvaluator)
    {
    }
}
