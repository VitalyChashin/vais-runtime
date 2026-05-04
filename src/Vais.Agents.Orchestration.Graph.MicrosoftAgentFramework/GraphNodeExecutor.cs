// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Vais.Agents.Core;

namespace Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

/// <summary>
/// MAF executor implementation per graph node. Receives a <see cref="GraphMessage"/>,
/// runs the node (Agent / Code / Interrupt / End), merges output into state, evaluates
/// outgoing edges via the shared <see cref="GraphPredicateEvaluator"/>, and forwards
/// the updated message to the next node via
/// <see cref="IWorkflowContext.SendMessageAsync(object, string, CancellationToken)"/>
/// (or <see cref="IWorkflowContext.YieldOutputAsync"/> for <c>End</c> nodes).
/// </summary>
/// <remarks>
/// Routing happens inside the executor — not via MAF's <c>AddEdge&lt;T&gt;(condition)</c>
/// API — because our edge predicates are async (handlerRef resolution) and MAF's
/// conditional-edge surface is synchronous. The edges declared on the
/// <see cref="WorkflowBuilder"/> are structural only (unconditional <c>AddEdge</c>);
/// the executor picks the right target dynamically using the same evaluator the
/// in-process orchestrator uses, keeping both paths semantically identical.
/// </remarks>
[SendsMessage(typeof(GraphMessage))]
[YieldsOutput(typeof(GraphMessage))]
internal class GraphNodeExecutor : Executor<GraphMessage>
{
    private readonly GraphNode _node;
    private readonly AgentGraphManifest _manifest;
    private readonly IAgentRegistry _registry;
    private readonly IAgentLifecycleManager _lifecycle;
    private readonly Func<GraphHandlerRef, IGraphEdgePredicate>? _predicateResolver;
    private readonly Func<GraphHandlerRef, IGraphEdgeEffect>? _effectResolver;
    private readonly Func<GraphHandlerRef, IGraphCodeNode>? _codeNodeResolver;
    private readonly Func<GraphHandlerRef, IGraphStateReducer>? _reducerResolver;
    private readonly AgentContext _context;
    private readonly IAgentRemoteInvoker? _remoteInvoker;
    private readonly IA2AGraphNodeInvoker? _a2aInvoker;
    private readonly string? _bearerToken;
    private readonly IGraphCheckpointer? _checkpointer;

    private readonly string? _hitlPortId;
    private readonly bool _isForkSource;

    public GraphNodeExecutor(
        GraphNode node,
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver,
        AgentContext context,
        IAgentRemoteInvoker? remoteInvoker = null,
        IA2AGraphNodeInvoker? a2aInvoker = null,
        string? bearerToken = null,
        IGraphCheckpointer? checkpointer = null,
        string? executorId = null,
        string? hitlPortId = null,
        bool isForkSource = false)
        : base(id: executorId ?? node.Id)
    {
        _node = node;
        _manifest = manifest;
        _registry = registry;
        _lifecycle = lifecycle;
        _predicateResolver = predicateResolver;
        _effectResolver = effectResolver;
        _codeNodeResolver = codeNodeResolver;
        _reducerResolver = reducerResolver;
        _context = context;
        _remoteInvoker = remoteInvoker;
        _a2aInvoker = a2aInvoker;
        _bearerToken = bearerToken;
        _checkpointer = checkpointer;
        _hitlPortId = hitlPortId;
        _isForkSource = isForkSource;
    }

    /// <summary>Exposes the manifest node's kind for <see cref="MafGraphBuilder"/>'s output-binding filter.</summary>
    public string NodeKind => _node.Kind;

    public override async ValueTask HandleAsync(GraphMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var state = message.State;

        // Resume semantics: when ResumeFromNodeId targets this executor, skip its body
        // and jump directly to outgoing-edge evaluation — the MAF equivalent of
        // InProcessGraphOrchestrator's skipNodeBody flag.
        var skipBody = message.ResumeFromNodeId is not null &&
                       string.Equals(message.ResumeFromNodeId, _node.Id, StringComparison.Ordinal);

        // Enforce the graph's max-step ceiling (not checked on the resume's first skip iteration,
        // matching InProcess semantics where the interrupt node itself doesn't count as a step).
        if (!skipBody && message.SuperStep >= message.MaxSteps)
        {
            throw new GraphRecursionException(_manifest.Id, message.MaxSteps);
        }

        // Terminal — End kind yields the final state out of the workflow.
        // The WithOutputFrom binding in MafGraphBuilder ensures this YieldOutputAsync
        // closes the run naturally; an explicit RequestHaltAsync was observed to
        // suppress the ExecutorCompletedEvent for this node, so we don't call it here.
        if (string.Equals(_node.Kind, "End", StringComparison.Ordinal))
        {
            if (_checkpointer is not null)
            {
                await _checkpointer.SaveAsync(new GraphCheckpoint(
                    message.RunId, _manifest.Id, _manifest.Version,
                    new Dictionary<string, JsonElement>(state),
                    NextNodeId: null, SuperStepIndex: message.SuperStep,
                    PendingInterruptId: null, IsComplete: true,
                    CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            var finalMessage = message with { SourceNodeId = _node.Id };
            await context.YieldOutputAsync(finalMessage).ConfigureAwait(false);
            return;
        }

        // Interrupt — save checkpoint, emit event, then either:
        //   HITL live-session mode (_hitlPortId set): stamp ResumeFromNodeId and forward to the
        //     RequestPort — the MAF workflow stays open; MafGraphOrchestrator.RunWithHitlAsync
        //     handles the RequestInfoEvent and feeds back the handler's response.
        //   Halt mode (_hitlPortId null): yield final message and RequestHaltAsync (existing behaviour).
        // On resume (skipBody), skip this block entirely and fall through to outgoing-edge evaluation.
        if (string.Equals(_node.Kind, "Interrupt", StringComparison.Ordinal) && !skipBody)
        {
            var interruptId = Guid.NewGuid().ToString("N");
            if (_checkpointer is not null)
            {
                // NextNodeId = _node.Id so crash-recovery via IResumableAgentGraph.ResumeAsync
                // re-enters the interrupt node; skipBody fires because ResumeFromNodeId == _node.Id,
                // which routes outgoing edges without re-running the interrupt body.
                await _checkpointer.SaveAsync(new GraphCheckpoint(
                    message.RunId, _manifest.Id, _manifest.Version,
                    new Dictionary<string, JsonElement>(state),
                    NextNodeId: _node.Id, SuperStepIndex: message.SuperStep,
                    PendingInterruptId: interruptId, IsComplete: false,
                    CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            await context.AddEventAsync(new GraphInterruptedEvent(_node.Id, interruptId, _node.InterruptReason)).ConfigureAwait(false);
            if (_hitlPortId is not null)
            {
                var hitlMessage = message with { SourceNodeId = _node.Id, ResumeFromNodeId = _node.Id };
                await context.SendMessageAsync(hitlMessage, _hitlPortId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var interruptedMessage = message with { SourceNodeId = _node.Id };
                await context.YieldOutputAsync(interruptedMessage).ConfigureAwait(false);
                await context.RequestHaltAsync().ConfigureAwait(false);
            }
            return;
        }

        // Execute the node body (Agent or Code) — skipped on the resume's first iteration
        // (skipBody) and skipped implicitly when this is an Interrupt node in resume mode
        // (the interrupt's outgoing edges are all we evaluate).
        if (!skipBody)
        {
            IReadOnlyDictionary<string, JsonElement> nodeOutput;
            if (string.Equals(_node.Kind, "Agent", StringComparison.Ordinal))
            {
                nodeOutput = await ExecuteAgentNodeAsync(state, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(_node.Kind, "Code", StringComparison.Ordinal))
            {
                if (_node.HandlerRef is null || _codeNodeResolver is null)
                {
                    throw new InvalidOperationException($"Code-kind node '{_node.Id}' needs a HandlerRef + code-node resolver.");
                }
                var handler = _codeNodeResolver(_node.HandlerRef);
                var input = FilterByInputBinding(state, _node.StateBindings);
                nodeOutput = await handler.ExecuteAsync(input, _context, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new NotSupportedException($"Unknown node kind '{_node.Kind}' on node '{_node.Id}'.");
            }

            // Merge node output into state (same reducer rules as the in-process orchestrator).
            if (nodeOutput.Count > 0)
            {
                var filtered = FilterByOutputBinding(nodeOutput, _node.StateBindings);
                var changed = await GraphStateReducers.MergeAsync(
                    state, filtered, _manifest.StateReducers, _reducerResolver, cancellationToken).ConfigureAwait(false);
                if (changed.Count > 0)
                {
                    await context.AddEventAsync(new StateUpdatedEvent(changed)).ConfigureAwait(false);
                }
            }

            // Per-step checkpoint — inside the body-execution block to mirror InProcess:
            // no checkpoint is written for the skipped-body resume iteration.
            if (_checkpointer is not null)
            {
                await _checkpointer.SaveAsync(new GraphCheckpoint(
                    message.RunId, _manifest.Id, _manifest.Version,
                    new Dictionary<string, JsonElement>(state),
                    NextNodeId: _node.Id, SuperStepIndex: message.SuperStep,
                    PendingInterruptId: null, IsComplete: false,
                    CreatedAt: DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
        }

        // Select next node by scanning outgoing edges in manifest order (first-match-wins).
        // Clear ResumeFromNodeId on all outgoing messages so downstream executors run normally.
        var baseOutgoing = skipBody ? message with { ResumeFromNodeId = null } : message;

        // Fork nodes: dispatch explicitly to every concurrent target and return.
        // AddFanOutEdge declares the topology for MAF visualisation and barrier tracking;
        // the executor drives delivery via SendMessageAsync (one per branch).
        if (_isForkSource)
        {
            var forkBase = baseOutgoing with { SuperStep = message.SuperStep + 1, SourceNodeId = _node.Id };
            foreach (var edge in _manifest.Edges.Where(e =>
                e.Concurrent && string.Equals(e.From, _node.Id, StringComparison.Ordinal)))
            {
                // Each branch gets its own state copy — branches mutate state independently.
                var branchMsg = forkBase with
                {
                    State = new Dictionary<string, JsonElement>(forkBase.State, StringComparer.Ordinal),
                };
                await context.AddEventAsync(new EdgeTraversedEvent(edge.From, edge.To)).ConfigureAwait(false);
                await context.SendMessageAsync(branchMsg, edge.To, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        GraphEdge? matchedEdge = null;
        foreach (var edge in _manifest.Edges.Where(e =>
            string.Equals(e.From, _node.Id, StringComparison.Ordinal)))
        {
            var matches = await GraphPredicateEvaluator.EvaluateAsync(
                edge.When, AsReadOnly(state), _predicateResolver, cancellationToken).ConfigureAwait(false);
            if (matches)
            {
                matchedEdge = edge;
                break;
            }
        }

        if (matchedEdge is null)
        {
            // No matching outgoing edge — treat as implicit completion.
            var implicitFinal = baseOutgoing with { SourceNodeId = _node.Id };
            await context.YieldOutputAsync(implicitFinal).ConfigureAwait(false);
            await context.RequestHaltAsync().ConfigureAwait(false);
            return;
        }

        // Apply edge side-effect + emit EdgeTraversed event.
        var effectChanges = await GraphEffectApplier.ApplyAsync(
            matchedEdge.OnTraverse, state, _effectResolver, cancellationToken).ConfigureAwait(false);
        if (effectChanges.Count > 0)
        {
            await context.AddEventAsync(new StateUpdatedEvent(effectChanges)).ConfigureAwait(false);
        }
        await context.AddEventAsync(new EdgeTraversedEvent(matchedEdge.From, matchedEdge.To)).ConfigureAwait(false);

        // Forward the updated message to the next node.
        var outgoing = baseOutgoing with
        {
            SuperStep = message.SuperStep + 1,
            SourceNodeId = _node.Id,
        };
        await context.SendMessageAsync(outgoing, matchedEdge.To, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyDictionary<string, JsonElement>> ExecuteAgentNodeAsync(
        IDictionary<string, JsonElement> state,
        CancellationToken cancellationToken)
    {
        if (_node.Ref is null)
        {
            throw new InvalidOperationException($"Agent-kind node '{_node.Id}' has no Ref.");
        }

        var text = BuildAgentInputText(state);
        AgentInvocationResult result;

        if (_node.Ref.RuntimeUrl is { } runtimeUrl)
        {
            // Cross-runtime path: forward to a remote runtime via IAgentRemoteInvoker.
            if (_remoteInvoker is null)
                throw new InvalidOperationException(
                    $"Node '{_node.Id}' has RuntimeUrl '{runtimeUrl}' but no IAgentRemoteInvoker was supplied to the executor.");

            var remoteHandle = new AgentHandle(_node.Ref.Id, _node.Ref.Version ?? string.Empty);
            result = await _remoteInvoker.InvokeAsync(
                runtimeUrl,
                remoteHandle,
                new AgentInvocationRequest(text, _context.UserId),
                _bearerToken,
                cancellationToken).ConfigureAwait(false);
        }
        else if (_node.Ref.A2AUrl is { } a2aUrl)
        {
            if (_a2aInvoker is null)
                throw new InvalidOperationException(
                    $"Node '{_node.Id}' has A2AUrl '{a2aUrl}' but no IA2AGraphNodeInvoker was supplied to the executor.");

            var responseText = await _a2aInvoker.InvokeAsync(
                a2aUrl,
                text,
                _bearerToken,
                cancellationToken).ConfigureAwait(false);

            result = new AgentInvocationResult(responseText);
        }
        else
        {
            var resolvedManifest = await _registry.GetAsync(_node.Ref.Id, _node.Ref.Version, cancellationToken).ConfigureAwait(false);
            if (resolvedManifest is null)
            {
                throw new InvalidOperationException(
                    $"Node '{_node.Id}' references agent '{_node.Ref.Id}' version '{_node.Ref.Version ?? "latest"}', but no matching manifest is registered.");
            }

            var handle = new AgentHandle(resolvedManifest.Id, resolvedManifest.Version);
            result = await _lifecycle.InvokeAsync(
                handle,
                new AgentInvocationRequest(text, _context.UserId),
                cancellationToken).ConfigureAwait(false);
        }

        var updates = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["lastAssistantText"] = JsonSerializer.SerializeToElement(result.Text),
        };
        var turnJson = JsonSerializer.SerializeToElement(new ChatTurn(AgentChatRole.Assistant, result.Text));
        updates[GraphStateReducers.WellKnownKey.Messages] = JsonSerializer.SerializeToElement(new[] { turnJson });

        if (_node.StateBindings?.Output is { Count: > 0 } && TryParseJsonObject(result.Text, out var parsed))
        {
            foreach (var prop in parsed.EnumerateObject())
            {
                updates[prop.Name] = prop.Value;
            }
        }
        return updates;
    }

    private static string BuildAgentInputText(IDictionary<string, JsonElement> state)
    {
        // Same fallback chain as InProcessGraphOrchestrator.
        if (state.TryGetValue(GraphStateReducers.WellKnownKey.Messages, out var messages) &&
            messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0)
        {
            var last = messages[messages.GetArrayLength() - 1];
            if (last.ValueKind == JsonValueKind.Object && last.TryGetProperty("Text", out var textProp))
            {
                var text = textProp.GetString();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        if (state.TryGetValue("query", out var query) && query.ValueKind == JsonValueKind.String)
        {
            var qtext = query.GetString();
            if (!string.IsNullOrEmpty(qtext)) return qtext;
        }
        return "(continue)";
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
            if (state.TryGetValue(key, out var value)) result[key] = value;
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
        var allowed = new HashSet<string>(keys, StringComparer.Ordinal)
        {
            GraphStateReducers.WellKnownKey.Messages,
            "lastAssistantText",
        };
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in nodeOutput)
        {
            if (allowed.Contains(key)) result[key] = value;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement> AsReadOnly(IDictionary<string, JsonElement> state)
        => state as IReadOnlyDictionary<string, JsonElement> ?? new Dictionary<string, JsonElement>(state, StringComparer.Ordinal);

    private static bool TryParseJsonObject(string text, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith('{')) return false;
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
