// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

namespace Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

/// <summary>
/// MAF executor for fan-in join nodes. Accumulates <see cref="GraphMessage"/> state
/// from N incoming branch messages (one <c>HandleAsync</c> call per
/// <c>AddFanInBarrierEdge</c> source) and only delegates
/// to the node body + routing on the final branch call.
/// </summary>
[SendsMessage(typeof(GraphMessage))]
[YieldsOutput(typeof(GraphMessage))]
internal sealed class GraphJoinNodeExecutor : GraphNodeExecutor
{
    private readonly int _incomingBranchCount;
    private int _receivedCount;
    private Dictionary<string, JsonElement>? _accumulated;
    // Guards _accumulated + _receivedCount. MAF barrier semantics serialize the
    // HandleAsync calls, but the lock is cheap insurance against future changes.
    private readonly object _lock = new();

    public GraphJoinNodeExecutor(
        GraphNode node,
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver,
        AgentContext context,
        IAgentRemoteInvoker? remoteInvoker,
        IA2AGraphNodeInvoker? a2aInvoker,
        string? bearerToken,
        IGraphCheckpointer? checkpointer,
        int incomingBranchCount)
        : base(node, manifest, registry, lifecycle, predicateResolver, effectResolver,
               codeNodeResolver, reducerResolver, context, remoteInvoker, a2aInvoker,
               bearerToken, checkpointer)
    {
        _incomingBranchCount = incomingBranchCount;
    }

    public override async ValueTask HandleAsync(
        GraphMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        bool isLastBranch;
        GraphMessage mergedMessage;

        lock (_lock)
        {
            _accumulated ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            // Last-write-wins merge. AgentGraphManifestValidator enforces non-overlapping
            // output bindings across branches, so this is deterministic.
            foreach (var (k, v) in message.State)
            {
                _accumulated[k] = v;
            }
            _receivedCount++;
            isLastBranch = _receivedCount >= _incomingBranchCount;
            mergedMessage = isLastBranch
                ? message with { State = new Dictionary<string, JsonElement>(_accumulated, StringComparer.Ordinal) }
                : message;
        }

        if (!isLastBranch)
        {
            // More branches still in flight — hold without executing the node body.
            return;
        }

        // All branches arrived. Delegate to base for body execution + routing.
        await base.HandleAsync(mergedMessage, context, cancellationToken).ConfigureAwait(false);
    }
}
