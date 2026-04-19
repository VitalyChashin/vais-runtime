// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

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
/// <b>Known gaps in v0.9:</b>
/// </para>
/// <list type="bullet">
///   <item><description>MAF-native conditional edges (<c>AddEdge&lt;T&gt;(source, target, condition)</c>) — unused; all routing happens inside the executor.</description></item>
///   <item><description>RequestPort-based HITL — interrupt nodes halt via <c>IWorkflowContext.RequestHaltAsync</c> + emit a <see cref="GraphInterrupted"/> event; durable resume lands in PR 4 alongside the OrleansCheckpointer.</description></item>
///   <item><description>MAF's <c>CheckpointManager</c> — wiring to our <see cref="IGraphCheckpointer"/> lands in PR 4.</description></item>
/// </list>
/// <para>
/// Consumers who want MAF's richer Workflow features (fan-out/fan-in, sub-workflows,
/// typed source-generated handlers, native conditional edges) can call
/// <see cref="MafGraphBuilder.Build"/> directly to get the <see cref="Workflow"/>
/// and use <c>InProcessExecution</c> themselves — the adapter is thin by design.
/// </para>
/// </remarks>
public class MafGraphOrchestrator<TState> : IAgentGraph<TState>
{
    private readonly AgentGraphManifest _manifest;
    private readonly IAgentRegistry _registry;
    private readonly IAgentLifecycleManager _lifecycle;
    private readonly Func<GraphHandlerRef, IGraphEdgePredicate>? _predicateResolver;
    private readonly Func<GraphHandlerRef, IGraphEdgeEffect>? _effectResolver;
    private readonly Func<GraphHandlerRef, IGraphCodeNode>? _codeNodeResolver;
    private readonly Func<string>? _runIdFactory;

    /// <summary>Default max-step ceiling matching the in-process orchestrator.</summary>
    public const int DefaultMaxSteps = 1000;

    /// <summary>Construct the MAF-backed orchestrator.</summary>
    public MafGraphOrchestrator(
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver = null,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver = null,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver = null,
        Func<string>? runIdFactory = null)
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
        _runIdFactory = runIdFactory;
    }

    /// <inheritdoc />
    public async ValueTask<TState> InvokeAsync(TState initial, AgentContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        IDictionary<string, JsonElement> bag = StateBagConverter.ToBag(initial);
        await foreach (var _ in RunAsync(bag, context, cancellationToken).ConfigureAwait(false))
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
        return RunAsync(bag, context, cancellationToken);
    }

    private async IAsyncEnumerable<AgentGraphEvent> RunAsync(
        IDictionary<string, JsonElement> state,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var runId = _runIdFactory?.Invoke() ?? Guid.NewGuid().ToString("N");
        var maxSteps = _manifest.MaxSteps ?? DefaultMaxSteps;

        var workflow = MafGraphBuilder.Build(
            _manifest,
            _registry,
            _lifecycle,
            _predicateResolver,
            _effectResolver,
            _codeNodeResolver);

        var watch = Stopwatch.StartNew();
        yield return new GraphStarted(
            DateTimeOffset.UtcNow, context, runId, 0,
            _manifest.Id, _manifest.Version, _manifest.Entry);

        var initialMessage = new GraphMessage(
            State: new Dictionary<string, JsonElement>(state, StringComparer.Ordinal),
            SuperStep: 0,
            RunId: runId,
            MaxSteps: maxSteps);

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
                yield return translated;
            }
        }

        if (recursionFailure is not null)
        {
            throw recursionFailure;
        }

        if (finalMessage is not null)
        {
            // Copy final state back to caller's bag so InvokeAsync sees the result.
            state.Clear();
            foreach (var (k, v) in finalMessage.State)
            {
                state[k] = v;
            }

            // Interrupted runs already emitted GraphInterrupted — don't also mark them Completed.
            // The caller resumes by re-invoking (durable resume semantics land in PR 4).
            if (!interrupted)
            {
                yield return new GraphCompleted(DateTimeOffset.UtcNow, context, runId, superStep,
                    finalMessage.SourceNodeId ?? _manifest.Entry, watch.Elapsed);
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
        Func<string>? runIdFactory = null)
        : base(manifest, registry, lifecycle, predicateResolver, effectResolver, codeNodeResolver, runIdFactory)
    {
    }
}
