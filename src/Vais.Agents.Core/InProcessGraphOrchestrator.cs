// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// Neutral in-process <see cref="IAgentGraph{TState}"/> implementation. Pregel/BSP
/// runtime: one node per super-step; outgoing edges evaluated in manifest order;
/// first-match-wins; checkpoint per super-step boundary. Zero external deps beyond
/// <c>Vais.Agents.Core</c>'s existing stack — works with any <see cref="ICompletionProvider"/>
/// (SK, MAF, or fake) and any <see cref="IAgentLifecycleManager"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same contract for typed + bag state.</b> Internally always operates on an
/// <see cref="IDictionary{TKey,TValue}"/> of <see cref="JsonElement"/>; the
/// <typeparamref name="TState"/> wrapper serialises the initial POCO via System.Text.Json
/// at <see cref="InvokeAsync"/> entry and deserialises the merged bag back into
/// <typeparamref name="TState"/> at exit. Bag-state callers (<see cref="IAgentGraph"/>)
/// use <see cref="IDictionary{String, JsonElement}"/> as <typeparamref name="TState"/>
/// directly, so the trips are no-ops.
/// </para>
/// </remarks>
public class InProcessGraphOrchestrator<TState> : IAgentGraph<TState>, IResumableAgentGraph<TState>, IHitlAgentGraph<TState>
{
    /// <summary>Default max-step ceiling matching LangGraph's <c>recursion_limit</c>.</summary>
    public const int DefaultMaxSteps = 1000;

    private static readonly ActivitySource _activitySource = new("Vais.Agents.Core.Graph", "1.0.0");

    private readonly AgentGraphManifest _manifest;
    private readonly IAgentRegistry _registry;
    private readonly IAgentLifecycleManager _lifecycle;
    private readonly IGraphCheckpointer _checkpointer;
    private readonly Func<GraphHandlerRef, IGraphEdgePredicate>? _predicateResolver;
    private readonly Func<GraphHandlerRef, IGraphEdgeEffect>? _effectResolver;
    private readonly Func<GraphHandlerRef, IGraphCodeNode>? _codeNodeResolver;
    private readonly Func<GraphHandlerRef, IGraphStateReducer>? _reducerResolver;
    private readonly Func<string>? _runIdFactory;
    private readonly IAgentRemoteInvoker? _remoteInvoker;
    private readonly IA2AGraphNodeInvoker? _a2aInvoker;
    private readonly string? _bearerToken;
    private readonly IAgentGraphEventBus _graphEventBus;
    private readonly IGraphExpressionEvaluator? _expressionEvaluator;

    /// <summary>Construct the orchestrator.</summary>
    /// <param name="manifest">Graph to run. Validated eagerly on first invocation.</param>
    /// <param name="registry">Agent registry — used to resolve <see cref="GraphAgentRef.Version"/> = null (latest) to a concrete version for lifecycle-manager handles.</param>
    /// <param name="lifecycle">Lifecycle manager for resolving + invoking <c>Agent</c>-kind nodes.</param>
    /// <param name="checkpointer">Checkpoint store. Pass <see cref="InMemoryCheckpointer"/> for tests.</param>
    /// <param name="predicateResolver">Resolver for <see cref="GraphEdgePredicate.HandlerRef"/> nodes. Null means handler-ref predicates throw.</param>
    /// <param name="effectResolver">Resolver for <see cref="GraphEdgeEffect.HandlerRef"/> nodes. Null means handler-ref effects throw.</param>
    /// <param name="codeNodeResolver">Resolver for <c>Code</c>-kind <see cref="GraphNode"/>s. Null means code-kind nodes throw.</param>
    /// <param name="reducerResolver">Resolver for <see cref="GraphStateReducer.HandlerRef"/> reducer declarations in <see cref="AgentGraphManifest.StateReducers"/>. Null means handler-ref reducers throw.</param>
    /// <param name="runIdFactory">Factory for the run id stamped on events + checkpoints. Null uses <c>Guid.NewGuid().ToString("N")</c>.</param>
    /// <param name="remoteInvoker">Invoker for cross-runtime agent nodes. Required when the graph manifest contains nodes with <see cref="GraphAgentRef.RuntimeUrl"/> set.</param>
    /// <param name="a2aInvoker">Invoker for A2A protocol agent nodes. Required when the graph manifest contains nodes with <see cref="GraphAgentRef.A2AUrl"/> set.</param>
    /// <param name="bearerToken">Bearer token forwarded to remote runtimes for identity propagation. Typically extracted from the inbound HTTP request by the caller.</param>
    /// <param name="graphEventBus">Bus to fan out graph lifecycle events to. Null uses <see cref="NullAgentGraphEventBus"/>.</param>
    /// <param name="expressionEvaluator">Evaluator for <see cref="GraphEdgePredicate.Expression"/> predicates. Null means expression predicates throw. Register via <c>AddPowerFxExpressionEvaluator()</c>.</param>
    public InProcessGraphOrchestrator(
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        IGraphCheckpointer checkpointer,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver = null,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver = null,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver = null,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver = null,
        Func<string>? runIdFactory = null,
        IAgentRemoteInvoker? remoteInvoker = null,
        IA2AGraphNodeInvoker? a2aInvoker = null,
        string? bearerToken = null,
        IAgentGraphEventBus? graphEventBus = null,
        IGraphExpressionEvaluator? expressionEvaluator = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(checkpointer);
        _manifest = manifest;
        _registry = registry;
        _lifecycle = lifecycle;
        _checkpointer = checkpointer;
        _predicateResolver = predicateResolver;
        _effectResolver = effectResolver;
        _codeNodeResolver = codeNodeResolver;
        _reducerResolver = reducerResolver;
        _runIdFactory = runIdFactory;
        _remoteInvoker = remoteInvoker;
        _a2aInvoker = a2aInvoker;
        _bearerToken = bearerToken;
        _graphEventBus = graphEventBus ?? NullAgentGraphEventBus.Instance;
        _expressionEvaluator = expressionEvaluator;
    }

    /// <inheritdoc />
    public async ValueTask<TState> InvokeAsync(TState initial, AgentContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        IDictionary<string, JsonElement> bag = ToBag(initial);

        await foreach (var _ in RunAsync(bag, context, startingNodeId: null, resumedRunId: null, hitlHandler: null, cancellationToken).ConfigureAwait(false))
        {
            // Drain the event stream; state is mutated in-place via `bag`.
        }

        return FromBag(bag);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AgentGraphEvent> StreamAsync(TState initial, AgentContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bag = ToBag(initial);
        return RunAsync(bag, context, startingNodeId: null, resumedRunId: null, hitlHandler: null, cancellationToken);
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

        // Rehydrate state from the checkpoint, then splice in the caller's resume payload
        // under the well-known key.
        var bag = new Dictionary<string, JsonElement>(checkpoint.State, StringComparer.Ordinal);
        if (resumePayload is not null)
        {
            bag["resume.payload"] = JsonSerializer.SerializeToElement(resumePayload);
        }

        await foreach (var _ in RunAsync(bag, context, checkpoint.NextNodeId, checkpoint.RunId, hitlHandler: null, cancellationToken).ConfigureAwait(false))
        {
            // Drain.
        }

        return FromBag(bag);
    }

    /// <summary>
    /// Stream variant of <see cref="ResumeAsync"/> — yields the full <see cref="AgentGraphEvent"/>
    /// taxonomy (starting with <see cref="GraphResumed"/>) just like <see cref="StreamAsync"/>.
    /// </summary>
    public IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(
        GraphCheckpoint checkpoint,
        TState? resumePayload,
        AgentContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(context);
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
        return RunAsync(bag, context, checkpoint.NextNodeId, checkpoint.RunId, hitlHandler: null, cancellationToken);
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
        var bag = ToBag(initial);
        return RunAsync(bag, context, startingNodeId: null, resumedRunId: null, hitlHandler: handleInterrupt, cancellationToken);
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
        IDictionary<string, JsonElement> bag = ToBag(initial);
        await foreach (var _ in RunAsync(bag, context, startingNodeId: null, resumedRunId: null, hitlHandler: handleInterrupt, cancellationToken).ConfigureAwait(false))
        {
            // Drain; GraphHitlAbortedException propagates naturally from the iterator.
        }
        return FromBag(bag);
    }

    private static IDictionary<string, JsonElement> ToBag(TState initial)
    {
        if (initial is IDictionary<string, JsonElement> already)
        {
            // Bag-state callers (IAgentGraph) skip the round-trip.
            return new Dictionary<string, JsonElement>(already, StringComparer.Ordinal);
        }
        if (initial is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        var json = JsonSerializer.SerializeToElement(initial);
        var bag = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in json.EnumerateObject())
            {
                bag[prop.Name] = prop.Value;
            }
        }
        return bag;
    }

    private static TState FromBag(IDictionary<string, JsonElement> bag)
    {
        if (typeof(TState) == typeof(IDictionary<string, JsonElement>))
        {
            return (TState)(object)bag;
        }
        var json = JsonSerializer.SerializeToElement(bag);
        return JsonSerializer.Deserialize<TState>(json)!;
    }

    private async IAsyncEnumerable<AgentGraphEvent> RunAsync(
        IDictionary<string, JsonElement> state,
        AgentContext context,
        string? startingNodeId,
        string? resumedRunId,
        Func<GraphInterrupted, CancellationToken, ValueTask<TState?>>? hitlHandler,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runId = resumedRunId ?? _runIdFactory?.Invoke() ?? Guid.NewGuid().ToString("N");
        var maxSteps = _manifest.MaxSteps ?? DefaultMaxSteps;
        var nodesById = _manifest.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        if (!nodesById.ContainsKey(_manifest.Entry))
        {
            throw new InvalidOperationException($"Entry node '{_manifest.Entry}' not found in graph '{_manifest.Id}'.");
        }

        // Detach from the ambient ASP.NET HttpRequestIn Activity before starting graph.run.
        // HttpRequestIn is created by ASP.NET with TraceFlags=None (unsampled) because the
        // Microsoft.AspNetCore source is not registered in our TracerProvider. The OTel
        // ParentBased sampler propagates that decision to every child, suppressing graph.run
        // and all descendants. Setting Current=null here forces graph.run to start as a
        // trace root, which the AlwaysOn leg of ParentBased(AlwaysOn) always samples.
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
        if (graphActivity != null && state.Count > 0)
            graphActivity.SetTag("langfuse.observation.input", JsonSerializer.Serialize(state));

        var watch = Stopwatch.StartNew();
        var superStep = 0;
        var currentNodeId = startingNodeId ?? _manifest.Entry;
        var isResume = startingNodeId is not null;

        if (isResume)
        {
            // Resume semantics: the starting node WAS an Interrupt that paused the graph.
            // Emit GraphResumed, then skip directly to evaluating the interrupt node's
            // outgoing edges. The interrupt node's body does not re-fire.
            var resumedInterruptId = state.TryGetValue("resume.interruptId", out var ii) && ii.ValueKind == JsonValueKind.String
                ? ii.GetString() ?? string.Empty
                : string.Empty;
            var resumedEvt = new GraphResumed(
                DateTimeOffset.UtcNow, context, runId, superStep,
                startingNodeId!, resumedInterruptId);
            await _graphEventBus.PublishAsync(resumedEvt, cancellationToken).ConfigureAwait(false);
            yield return resumedEvt;
        }
        else
        {
            var startedEvt = new GraphStarted(
                DateTimeOffset.UtcNow, context, runId, superStep,
                _manifest.Id, _manifest.Version, _manifest.Entry);
            await _graphEventBus.PublishAsync(startedEvt, cancellationToken).ConfigureAwait(false);
            yield return startedEvt;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (superStep >= maxSteps)
            {
                var ex = new GraphRecursionException(_manifest.Id, maxSteps);
                graphActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                var recursionEvt = new GraphFailed(DateTimeOffset.UtcNow, context, runId, superStep,
                    ex.GetType().Name, ex.Message, watch.Elapsed);
                await _graphEventBus.PublishAsync(recursionEvt, cancellationToken).ConfigureAwait(false);
                yield return recursionEvt;
                throw ex;
            }

            if (!nodesById.TryGetValue(currentNodeId, out var node))
            {
                var err = new InvalidOperationException($"Graph references unknown node '{currentNodeId}'.");
                graphActivity?.SetStatus(ActivityStatusCode.Error, err.Message);
                var unknownNodeEvt = new GraphFailed(DateTimeOffset.UtcNow, context, runId, superStep,
                    err.GetType().Name, err.Message, watch.Elapsed);
                await _graphEventBus.PublishAsync(unknownNodeEvt, cancellationToken).ConfigureAwait(false);
                yield return unknownNodeEvt;
                throw err;
            }

            // Terminal — End kind completes the graph.
            if (string.Equals(node.Kind, "End", StringComparison.Ordinal))
            {
                await _checkpointer.SaveAsync(new GraphCheckpoint(
                    runId, _manifest.Id, _manifest.Version, new Dictionary<string, JsonElement>(state),
                    NextNodeId: null, SuperStepIndex: superStep, PendingInterruptId: null,
                    IsComplete: true, CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                graphActivity?.SetTag("langfuse.observation.output", JsonSerializer.Serialize(state));
                var endCompletedEvt = new GraphCompleted(DateTimeOffset.UtcNow, context, runId, superStep,
                    currentNodeId, watch.Elapsed, new Dictionary<string, JsonElement>(state));
                await _graphEventBus.PublishAsync(endCompletedEvt, cancellationToken).ConfigureAwait(false);
                yield return endCompletedEvt;
                yield break;
            }

            // Interrupt kind — emit event, checkpoint, then either pause (halt-mode) or
            // invoke the HITL handler inline and continue (live-mode).
            // On resume (isResume && first iteration), skip re-firing so the graph continues
            // past this node via outgoing-edge evaluation below.
            if (string.Equals(node.Kind, "Interrupt", StringComparison.Ordinal) && !isResume)
            {
                var interruptId = Guid.NewGuid().ToString("N");
                await _checkpointer.SaveAsync(new GraphCheckpoint(
                    runId, _manifest.Id, _manifest.Version, new Dictionary<string, JsonElement>(state),
                    NextNodeId: currentNodeId, SuperStepIndex: superStep, PendingInterruptId: interruptId,
                    IsComplete: false, CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                var interruptedEvt = new GraphInterrupted(DateTimeOffset.UtcNow, context, runId, superStep,
                    currentNodeId, interruptId, node.InterruptReason);
                await _graphEventBus.PublishAsync(interruptedEvt, cancellationToken).ConfigureAwait(false);
                yield return interruptedEvt;

                if (hitlHandler is not null)
                {
                    // Live HITL mode: call the handler and continue the graph.
                    var response = await hitlHandler(interruptedEvt, cancellationToken).ConfigureAwait(false);
                    if (response is null)
                    {
                        var abortEx = new GraphHitlAbortedException(currentNodeId);
                        var failedEvt = new GraphFailed(DateTimeOffset.UtcNow, context, runId, superStep,
                            abortEx.GetType().Name, abortEx.Message, watch.Elapsed);
                        await _graphEventBus.PublishAsync(failedEvt, cancellationToken).ConfigureAwait(false);
                        yield return failedEvt;
                        throw abortEx;
                    }
                    var hitlPayload = new Dictionary<string, JsonElement>
                    {
                        ["hitl.response"] = JsonSerializer.SerializeToElement(response),
                    };
                    var hitlChanged = await GraphStateReducers.MergeAsync(
                        state, hitlPayload, _manifest.StateReducers, _reducerResolver, cancellationToken).ConfigureAwait(false);
                    if (hitlChanged.Count > 0)
                    {
                        var hitlStateEvt = new StateUpdated(DateTimeOffset.UtcNow, context, runId, superStep, hitlChanged);
                        await _graphEventBus.PublishAsync(hitlStateEvt, cancellationToken).ConfigureAwait(false);
                        yield return hitlStateEvt;
                    }
                    // Signal the loop to skip re-executing the interrupt node's body
                    // and jump straight to outgoing-edge evaluation.
                    isResume = true;
                }
                else
                {
                    yield break;
                }
            }

            // Resume path on the first iteration: skip the interrupt node's body execution
            // (it would just pause again) and jump to edge evaluation below. After the first
            // iteration `isResume` is reset so any subsequent interrupts fire normally.
            var skipNodeBody = isResume;
            if (isResume)
            {
                isResume = false;
            }

            if (!skipNodeBody)
            {
                // Node execution.
                // Async-iterator yields return to the caller's ExecutionContext (where
                // HttpRequestIn is Activity.Current). Restore graphActivity before starting
                // the graph.node child span so the parent is correct, not the HTTP request.
                if (graphActivity != null) Activity.Current = graphActivity;
                using var nodeActivity = _activitySource.StartActivity("graph.node", ActivityKind.Internal);
                nodeActivity?.SetTag("graph.run_id",    runId);
                nodeActivity?.SetTag("graph.node.id",   currentNodeId);
                nodeActivity?.SetTag("graph.node.kind", node.Kind);
                if (node.Ref?.Id is { } agentRefId)
                    nodeActivity?.SetTag("vais.agent.name", agentRefId);
                // Set gen_ai.prompt to the text the agent will receive — same attribute grain.ask uses,
                // proven to map to Langfuse's Input column for non-generation spans.
                // Use the binding-filtered state so nodes that don't declare `messages` as an input
                // key receive their declared primary key (e.g. `query`) rather than the last message.
                var nodeFilteredInput = FilterByInputBinding(state, node.StateBindings);
                nodeActivity?.SetTag("gen_ai.prompt", BuildAgentInputText(nodeFilteredInput, node.StateBindings));

                var nodeStartedEvt = new NodeStarted(DateTimeOffset.UtcNow, context, runId, superStep, currentNodeId, node.Kind);
                await _graphEventBus.PublishAsync(nodeStartedEvt, cancellationToken).ConfigureAwait(false);
                yield return nodeStartedEvt;
                // Re-anchor after yield — same EC loss applies here.
                if (nodeActivity != null) Activity.Current = nodeActivity;
                var nodeWatch = Stopwatch.StartNew();
                IReadOnlyDictionary<string, JsonElement>? nodeOutput = null;
                Exception? nodeFailure = null;
                try
                {
                    nodeOutput = await ExecuteNodeAsync(node, state, context, runId, superStep, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    nodeFailure = ex;
                }
                if (nodeFailure is not null)
                {
                    nodeActivity?.SetStatus(ActivityStatusCode.Error, nodeFailure.Message);
                    graphActivity?.SetStatus(ActivityStatusCode.Error, nodeFailure.Message);
                    var nodeFailEvt = new GraphFailed(DateTimeOffset.UtcNow, context, runId, superStep,
                        nodeFailure.GetType().Name, nodeFailure.Message, watch.Elapsed);
                    await _graphEventBus.PublishAsync(nodeFailEvt, cancellationToken).ConfigureAwait(false);
                    yield return nodeFailEvt;
                    throw nodeFailure;
                }
                nodeWatch.Stop();
                nodeActivity?.SetStatus(ActivityStatusCode.Ok);
                if (nodeActivity != null &&
                    nodeOutput is { Count: > 0 } &&
                    nodeOutput.TryGetValue("lastAssistantText", out var lastText) &&
                    lastText.ValueKind == JsonValueKind.String)
                {
                    nodeActivity.SetTag("gen_ai.completion", lastText.GetString()!);
                }
                var nodeCompletedEvt = new NodeCompleted(DateTimeOffset.UtcNow, context, runId, superStep,
                    currentNodeId, node.Kind, nodeWatch.Elapsed);
                await _graphEventBus.PublishAsync(nodeCompletedEvt, cancellationToken).ConfigureAwait(false);
                yield return nodeCompletedEvt;

                // Merge node output into state (honouring the node's StateBindings.Output filter if any).
                if (nodeOutput is { Count: > 0 })
                {
                    var filtered = FilterByOutputBinding(nodeOutput, node.StateBindings);
                    var changed = await GraphStateReducers.MergeAsync(
                        state, filtered, _manifest.StateReducers, _reducerResolver, cancellationToken).ConfigureAwait(false);
                    if (changed.Count > 0)
                    {
                        var stateUpdatedEvt = new StateUpdated(DateTimeOffset.UtcNow, context, runId, superStep, changed);
                        await _graphEventBus.PublishAsync(stateUpdatedEvt, cancellationToken).ConfigureAwait(false);
                        yield return stateUpdatedEvt;
                    }
                }

                // Persist per-super-step.
                await _checkpointer.SaveAsync(new GraphCheckpoint(
                    runId, _manifest.Id, _manifest.Version, new Dictionary<string, JsonElement>(state),
                    NextNodeId: currentNodeId, SuperStepIndex: superStep, PendingInterruptId: null,
                    IsComplete: false, CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }

            // Select next node via the first matching outgoing edge.
            string? nextNodeId = null;
            foreach (var edge in _manifest.Edges.Where(e => string.Equals(e.From, currentNodeId, StringComparison.Ordinal)))
            {
                var matches = await GraphPredicateEvaluator.EvaluateAsync(
                    edge.When, AsReadOnly(state), _predicateResolver, cancellationToken, _expressionEvaluator).ConfigureAwait(false);
                if (!matches)
                {
                    continue;
                }

                // Apply edge side-effect if any.
                var effectChanges = await GraphEffectApplier.ApplyAsync(
                    edge.OnTraverse, state, _effectResolver, cancellationToken).ConfigureAwait(false);
                if (effectChanges.Count > 0)
                {
                    var effectStateEvt = new StateUpdated(DateTimeOffset.UtcNow, context, runId, superStep, effectChanges);
                    await _graphEventBus.PublishAsync(effectStateEvt, cancellationToken).ConfigureAwait(false);
                    yield return effectStateEvt;
                }

                var edgeTraversedEvt = new EdgeTraversed(DateTimeOffset.UtcNow, context, runId, superStep, edge.From, edge.To);
                await _graphEventBus.PublishAsync(edgeTraversedEvt, cancellationToken).ConfigureAwait(false);
                yield return edgeTraversedEvt;
                nextNodeId = edge.To;
                break;
            }

            if (nextNodeId is null)
            {
                // No matching outgoing edge — treat as implicit completion.
                var implicitCompletedEvt = new GraphCompleted(DateTimeOffset.UtcNow, context, runId, superStep,
                    currentNodeId, watch.Elapsed, new Dictionary<string, JsonElement>(state));
                await _graphEventBus.PublishAsync(implicitCompletedEvt, cancellationToken).ConfigureAwait(false);
                yield return implicitCompletedEvt;
                yield break;
            }

            currentNodeId = nextNodeId;
            superStep++;
        }
    }

    private async ValueTask<IReadOnlyDictionary<string, JsonElement>> ExecuteNodeAsync(
        GraphNode node,
        IDictionary<string, JsonElement> state,
        AgentContext context,
        string runId,
        int superStep,
        CancellationToken cancellationToken)
    {
        if (string.Equals(node.Kind, "Agent", StringComparison.Ordinal))
        {
            if (node.Ref is null)
            {
                throw new InvalidOperationException($"Agent-kind node '{node.Id}' has no Ref.");
            }

            // Build invocation request from state bindings (shared by local + remote paths).
            // Filter state to declared input keys first so `messages` doesn't shadow other keys
            // for nodes that don't list `messages` in their input binding.
            var filteredInput = FilterByInputBinding(state, node.StateBindings);
            var text = BuildAgentInputText(filteredInput, node.StateBindings);
            var metadata = BuildMetadata(filteredInput, node.StateBindings);

            AgentInvocationResult result;
            string agentId;

            if (node.Ref.RuntimeUrl is { } runtimeUrl)
            {
                // Cross-runtime path: forward to a remote runtime via IAgentRemoteInvoker.
                if (_remoteInvoker is null)
                    throw new InvalidOperationException(
                        $"Node '{node.Id}' has RuntimeUrl '{runtimeUrl}' but no IAgentRemoteInvoker was supplied to the orchestrator.");

                agentId = node.Ref.Id;
                var remoteHandle = new AgentHandle(node.Ref.Id, node.Ref.Version ?? string.Empty);
                result = await _remoteInvoker.InvokeAsync(
                    runtimeUrl,
                    remoteHandle,
                    new AgentInvocationRequest(text, context.UserId, metadata),
                    _bearerToken,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (node.Ref.A2AUrl is { } a2aUrl)
            {
                // A2A protocol path: invoke remote agent via Agent-to-Agent protocol.
                if (_a2aInvoker is null)
                    throw new InvalidOperationException(
                        $"Node '{node.Id}' has A2AUrl '{a2aUrl}' but no IA2AGraphNodeInvoker was supplied to the orchestrator.");

                agentId = node.Ref.Id;
                var responseText = await _a2aInvoker.InvokeAsync(
                    a2aUrl,
                    text,
                    _bearerToken,
                    cancellationToken).ConfigureAwait(false);

                result = new AgentInvocationResult(responseText);
            }
            else
            {
                // Resolve to a concrete version via the registry — "latest" / null lookups must
                // land on the actual version the lifecycle manager keyed on at CreateAsync time.
                var resolvedManifest = await _registry.GetAsync(node.Ref.Id, node.Ref.Version, cancellationToken).ConfigureAwait(false);
                if (resolvedManifest is null)
                {
                    throw new InvalidOperationException(
                        $"Node '{node.Id}' references agent '{node.Ref.Id}' version '{node.Ref.Version ?? "latest"}', but no matching manifest is registered.");
                }

                agentId = resolvedManifest.Id;
                var handle = new AgentHandle(resolvedManifest.Id, resolvedManifest.Version);
                result = await _lifecycle.InvokeAsync(
                    handle,
                    new AgentInvocationRequest(text, context.UserId, metadata),
                    cancellationToken).ConfigureAwait(false);
            }

            var invokedEvt = new NodeAgentInvoked(DateTimeOffset.UtcNow, context, runId, superStep,
                node.Id, agentId, TruncateText(text), TruncateText(result.Text ?? string.Empty), 0, 0);
            await _graphEventBus.PublishAsync(invokedEvt, cancellationToken).ConfigureAwait(false);

            // Project the agent's reply into state: raw text under "lastAssistantText",
            // plus parsed structured output if the reply is JSON (StateBindings.Output
            // filters which fields land in state).
            var updates = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["lastAssistantText"] = JsonSerializer.SerializeToElement(result.Text),
            };

            // Append the turn to messages history (AppendMessages reducer handles it).
            var turnJson = JsonSerializer.SerializeToElement(new ChatTurn(AgentChatRole.Assistant, result.Text ?? string.Empty));
            updates[GraphStateReducers.WellKnownKey.Messages] = JsonSerializer.SerializeToElement(new[] { turnJson });

            // If the reply text parses as JSON object and the node has an output binding,
            // merge the parsed shape into state.
            if (node.StateBindings?.Output is { Count: > 0 } && TryParseJsonObject(result.Text ?? string.Empty, out var parsed))
            {
                foreach (var prop in parsed.EnumerateObject())
                {
                    updates[prop.Name] = prop.Value;
                }
            }
            return updates;
        }

        if (string.Equals(node.Kind, "Code", StringComparison.Ordinal))
        {
            if (node.HandlerRef is null)
            {
                throw new InvalidOperationException($"Code-kind node '{node.Id}' has no HandlerRef.");
            }
            if (_codeNodeResolver is null)
            {
                throw new InvalidOperationException(
                    $"Code-kind node '{node.Id}' references handler '{node.HandlerRef.TypeName}' but no code-node resolver was supplied.");
            }
            var handler = _codeNodeResolver(node.HandlerRef);
            var input = FilterByInputBinding(state, node.StateBindings);
            return await handler.ExecuteAsync(input, context, cancellationToken).ConfigureAwait(false);
        }

        throw new NotSupportedException($"Unknown node kind '{node.Kind}' on node '{node.Id}'.");
    }

    /// <summary>Placeholder text used when a graph step has no message + no query in state. Non-empty so downstream agents' non-empty-text validation doesn't trip.</summary>
    internal const string DefaultAgentInputText = "(continue)";

    private static string BuildAgentInputText(
        IReadOnlyDictionary<string, JsonElement> state,
        GraphStateBindings? bindings)
    {
        // Resolve order: last message in `messages` → `query` state key → placeholder.
        // Richer templating (input-binding interpolation into a prompt) is post-v0.9.
        if (state.TryGetValue(GraphStateReducers.WellKnownKey.Messages, out var messages) &&
            messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0)
        {
            var last = messages[messages.GetArrayLength() - 1];
            if (last.ValueKind == JsonValueKind.Object && last.TryGetProperty("Text", out var textProp))
            {
                var text = textProp.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        if (state.TryGetValue("query", out var query) && query.ValueKind == JsonValueKind.String)
        {
            var qtext = query.GetString();
            if (!string.IsNullOrEmpty(qtext))
            {
                return qtext;
            }
        }

        return DefaultAgentInputText;
    }

    private static IReadOnlyDictionary<string, string>? BuildMetadata(
        IReadOnlyDictionary<string, JsonElement> state,
        GraphStateBindings? bindings)
    {
        if (bindings?.Input is not { Count: > 0 } keys)
        {
            return null;
        }
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (state.TryGetValue(key, out var value))
            {
                metadata[key] = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            }
        }
        return metadata.Count == 0 ? null : metadata;
    }

    private static IReadOnlyDictionary<string, JsonElement> FilterByInputBinding(
        IDictionary<string, JsonElement> state,
        GraphStateBindings? bindings)
    {
        if (bindings?.Input is not { Count: > 0 } keys)
        {
            return new Dictionary<string, JsonElement>(state, StringComparer.Ordinal);
        }
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (state.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> FilterByOutputBinding(
        IReadOnlyDictionary<string, JsonElement> nodeOutput,
        GraphStateBindings? bindings)
    {
        if (bindings?.Output is not { Count: > 0 } keys)
        {
            return nodeOutput;
        }
        // Always allow the well-known messages key through — agent nodes emit it unconditionally
        // via the AppendMessages reducer convention.
        var allowed = new HashSet<string>(keys, StringComparer.Ordinal) { GraphStateReducers.WellKnownKey.Messages, "lastAssistantText" };
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in nodeOutput)
        {
            if (allowed.Contains(key))
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> AsReadOnly(IDictionary<string, JsonElement> state)
        => state as IReadOnlyDictionary<string, JsonElement> ?? new Dictionary<string, JsonElement>(state, StringComparer.Ordinal);

    private static string TruncateText(string s, int maxChars = 8192) =>
        s.Length <= maxChars ? s : string.Concat(s.AsSpan(0, maxChars), "…");

    private static bool TryParseJsonObject(string text, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        try
        {
            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith('{'))
            {
                return false;
            }
            var doc = JsonDocument.Parse(trimmed);
            element = doc.RootElement.Clone();
            return element.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Non-generic <see cref="InProcessGraphOrchestrator{TState}"/> specialisation over
/// <see cref="IDictionary{TKey,TValue}"/> of <see cref="JsonElement"/> — the state
/// shape used by declarative YAML-authored graphs.
/// </summary>
public sealed class InProcessGraphOrchestrator : InProcessGraphOrchestrator<IDictionary<string, JsonElement>>, IAgentGraph
{
    /// <inheritdoc />
    public InProcessGraphOrchestrator(
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        IGraphCheckpointer checkpointer,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver = null,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver = null,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver = null,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver = null,
        Func<string>? runIdFactory = null,
        IAgentRemoteInvoker? remoteInvoker = null,
        IA2AGraphNodeInvoker? a2aInvoker = null,
        string? bearerToken = null,
        IAgentGraphEventBus? graphEventBus = null,
        IGraphExpressionEvaluator? expressionEvaluator = null)
        : base(manifest, registry, lifecycle, checkpointer, predicateResolver, effectResolver, codeNodeResolver, reducerResolver, runIdFactory, remoteInvoker, a2aInvoker, bearerToken, graphEventBus, expressionEvaluator)
    {
    }
}
