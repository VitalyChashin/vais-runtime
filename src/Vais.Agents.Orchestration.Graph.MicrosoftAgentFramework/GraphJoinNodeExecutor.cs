// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Extensions;

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
    private readonly AgentGraphManifest _joinManifest;
    private readonly Func<GraphHandlerRef, IGraphStateReducer>? _joinReducerResolver;
    private int _receivedCount;
    private Dictionary<string, JsonElement>? _accumulated;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

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
        int incomingBranchCount,
        IReadOnlyList<AgentInputMiddleware>? inputMiddleware = null,
        IExtensionChainComposer? graphNodeComposer = null,
        ILogger? logger = null)
        : base(node, manifest, registry, lifecycle, predicateResolver, effectResolver,
               codeNodeResolver, reducerResolver, context, remoteInvoker, a2aInvoker,
               bearerToken, checkpointer, inputMiddleware: inputMiddleware,
               graphNodeComposer: graphNodeComposer, logger: logger)
    {
        _incomingBranchCount = incomingBranchCount;
        _joinManifest = manifest;
        _joinReducerResolver = reducerResolver;
    }

    public override async ValueTask HandleAsync(
        GraphMessage message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        bool isLastBranch;
        GraphMessage mergedMessage;

        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _accumulated ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            await GraphStateReducers.MergeAsync(
                _accumulated, (IReadOnlyDictionary<string, JsonElement>)message.State,
                _joinManifest.StateReducers, _joinReducerResolver,
                cancellationToken).ConfigureAwait(false);
            _receivedCount++;
            isLastBranch = _receivedCount >= _incomingBranchCount;
            mergedMessage = isLastBranch
                ? message with { State = new Dictionary<string, JsonElement>(_accumulated, StringComparer.Ordinal), FanoutContext = null }
                : message;
        }
        finally
        {
            _semaphore.Release();
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
