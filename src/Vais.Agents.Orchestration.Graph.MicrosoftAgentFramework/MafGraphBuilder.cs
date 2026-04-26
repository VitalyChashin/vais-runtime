// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Agents.AI.Workflows;

namespace Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

/// <summary>
/// Result returned by <see cref="MafGraphBuilder.BuildForHitl"/> — bundles the MAF workflow
/// with the port-id → node-id map needed by the HITL streaming loop to resolve
/// <see cref="RequestInfoEvent"/> port ids back to interrupt-node ids.
/// </summary>
public readonly record struct MafGraphBuildResult(
    Workflow Workflow,
    IReadOnlyDictionary<string, string> PortIdToNodeId);

/// <summary>
/// Projects an <see cref="AgentGraphManifest"/> onto a MAF <see cref="Workflow"/>.
/// Consumers who want MAF's richer Workflow features (custom checkpoint managers,
/// visualisation via <c>WorkflowVisualizer</c>, embedding in a larger workflow)
/// can call <see cref="Build"/> directly to get the <see cref="Workflow"/> and
/// run it via <c>InProcessExecution</c> themselves.
/// </summary>
public static class MafGraphBuilder
{
    /// <summary>Build a MAF <see cref="Workflow"/> for <paramref name="manifest"/>.</summary>
    /// <param name="manifest">Graph manifest to project.</param>
    /// <param name="registry">Agent registry for resolving <c>Agent</c>-kind nodes.</param>
    /// <param name="lifecycle">Lifecycle manager for invoking resolved agents.</param>
    /// <param name="predicateResolver">Resolver for <see cref="GraphEdgePredicate.HandlerRef"/> nodes. Null means handler-ref predicates throw.</param>
    /// <param name="effectResolver">Resolver for <see cref="GraphEdgeEffect.HandlerRef"/> nodes.</param>
    /// <param name="codeNodeResolver">Resolver for <c>Code</c>-kind <see cref="GraphNode"/>s.</param>
    /// <param name="reducerResolver">Resolver for <see cref="GraphStateReducer.HandlerRef"/> reducer declarations in <see cref="AgentGraphManifest.StateReducers"/>. Null means handler-ref reducers throw.</param>
    /// <param name="context">Ambient agent context stamped on each node invocation. Uses a default empty context when null.</param>
    /// <param name="remoteInvoker">Invoker for cross-runtime agent nodes. Required when the graph manifest contains nodes with <see cref="GraphAgentRef.RuntimeUrl"/> set.</param>
    /// <param name="a2aInvoker">Invoker for A2A protocol agent nodes. Required when the graph manifest contains nodes with <see cref="GraphAgentRef.A2AUrl"/> set.</param>
    /// <param name="bearerToken">Bearer token forwarded to remote runtimes for identity propagation.</param>
    /// <param name="startNodeId">
    /// Override the workflow entry executor. Null (the default) uses <see cref="AgentGraphManifest.Entry"/>.
    /// Pass the interrupt node's id on resume so <see cref="InProcessExecution"/> delivers
    /// the initial <see cref="GraphMessage"/> directly to that executor, which then skips its
    /// body and evaluates outgoing edges via its <see cref="GraphMessage.ResumeFromNodeId"/> flag.
    /// </param>
    /// <param name="checkpointer">
    /// Checkpointer wired into each <see cref="GraphNodeExecutor"/>. Null skips all checkpoint
    /// saves (identical to v0.9 behaviour). Required for <see cref="IResumableAgentGraph{TState}"/>.
    /// </param>
    public static Workflow Build(
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver = null,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver = null,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver = null,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver = null,
        AgentContext? context = null,
        IAgentRemoteInvoker? remoteInvoker = null,
        IA2AGraphNodeInvoker? a2aInvoker = null,
        string? bearerToken = null,
        string? startNodeId = null,
        IGraphCheckpointer? checkpointer = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifecycle);
        context ??= new AgentContext();

        // Build one Executor per node keyed by node id.
        var executors = new Dictionary<string, GraphNodeExecutor>(StringComparer.Ordinal);
        foreach (var node in manifest.Nodes)
        {
            executors[node.Id] = new GraphNodeExecutor(
                node, manifest, registry, lifecycle,
                predicateResolver, effectResolver, codeNodeResolver,
                reducerResolver, context, remoteInvoker, a2aInvoker, bearerToken, checkpointer);
        }

        var effectiveStart = startNodeId ?? manifest.Entry;
        if (!executors.TryGetValue(effectiveStart, out var startExecutor))
        {
            throw new InvalidOperationException($"Start node '{effectiveStart}' not found in graph '{manifest.Id}'.");
        }

        var builder = new WorkflowBuilder(ExecutorBindingExtensions.BindExecutor(startExecutor))
            .WithName(manifest.Id)
            .WithDescription(manifest.Description ?? $"Vais agent graph '{manifest.Id}' v{manifest.Version}.");

        // Declare edges structurally — routing is handled inside the executor
        // (see GraphNodeExecutor.HandleAsync). MAF needs the edges declared for
        // validation + visualisation; the actual conditional logic is ours.
        foreach (var edge in manifest.Edges)
        {
            if (!executors.TryGetValue(edge.From, out var fromExec) ||
                !executors.TryGetValue(edge.To, out var toExec))
            {
                continue; // Validator catches these; skip here to keep the builder minimal.
            }
            builder.AddEdge(
                ExecutorBindingExtensions.BindExecutor(fromExec),
                ExecutorBindingExtensions.BindExecutor(toExec));
        }

        // Mark End + Interrupt nodes as output executors — MAF's MaterializeResponseAsync
        // only captures YieldOutputAsync events from executors in this list.
        var outputBindings = executors.Values
            .Where(e => string.Equals(e.NodeKind, "End", StringComparison.Ordinal) ||
                        string.Equals(e.NodeKind, "Interrupt", StringComparison.Ordinal))
            .Select(e => ExecutorBindingExtensions.BindExecutor(e))
            .ToArray();
        if (outputBindings.Length > 0)
        {
            builder = builder.WithOutputFrom(outputBindings);
        }

        return builder.Build(validateOrphans: false);
    }

    /// <summary>
    /// Build a HITL-capable MAF <see cref="Workflow"/> for <paramref name="manifest"/>.
    /// Each <c>Interrupt</c>-kind node is expanded into three MAF executors:
    /// <list type="bullet">
    ///   <item><description><c>{id}</c> — emitter: emits <see cref="GraphInterruptedEvent"/>, forwards to the RequestPort.</description></item>
    ///   <item><description><c>{id}_hitl</c> — <see cref="RequestPort{TReq,TResp}"/>: MAF blocks here and emits a <see cref="RequestInfoEvent"/>.</description></item>
    ///   <item><description><c>{id}_hitl_resume</c> — resume router: same <see cref="GraphNode"/>, distinct executor id; <see cref="GraphMessage.ResumeFromNodeId"/> == node id causes body skip and outgoing-edge evaluation.</description></item>
    /// </list>
    /// </summary>
    /// <returns>
    /// A <see cref="MafGraphBuildResult"/> with the built workflow and a map from port id to interrupt-node id,
    /// used by <c>MafGraphOrchestrator.StreamWithHitlAsync</c> to resolve <see cref="RequestInfoEvent"/> sources.
    /// </returns>
    public static MafGraphBuildResult BuildForHitl(
        AgentGraphManifest manifest,
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        Func<GraphHandlerRef, IGraphEdgePredicate>? predicateResolver = null,
        Func<GraphHandlerRef, IGraphEdgeEffect>? effectResolver = null,
        Func<GraphHandlerRef, IGraphCodeNode>? codeNodeResolver = null,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver = null,
        AgentContext? context = null,
        IAgentRemoteInvoker? remoteInvoker = null,
        IA2AGraphNodeInvoker? a2aInvoker = null,
        string? bearerToken = null,
        IGraphCheckpointer? checkpointer = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifecycle);
        context ??= new AgentContext();

        var executors = new Dictionary<string, GraphNodeExecutor>(StringComparer.Ordinal);
        var hitlPorts = new Dictionary<string, RequestPort<GraphMessage, GraphMessage>>(StringComparer.Ordinal); // nodeId → port
        var portIdToNodeId = new Dictionary<string, string>(StringComparer.Ordinal);
        var interruptNodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in manifest.Nodes)
        {
            if (string.Equals(node.Kind, "Interrupt", StringComparison.Ordinal))
            {
                interruptNodeIds.Add(node.Id);
                var portId = $"{node.Id}_hitl";
                var resumeId = $"{node.Id}_hitl_resume";

                executors[node.Id] = new GraphNodeExecutor(
                    node, manifest, registry, lifecycle,
                    predicateResolver, effectResolver, codeNodeResolver,
                    reducerResolver, context, remoteInvoker, a2aInvoker, bearerToken, checkpointer,
                    hitlPortId: portId);

                var port = RequestPort.Create<GraphMessage, GraphMessage>(portId);
                hitlPorts[node.Id] = port;
                portIdToNodeId[portId] = node.Id;

                executors[resumeId] = new GraphNodeExecutor(
                    node, manifest, registry, lifecycle,
                    predicateResolver, effectResolver, codeNodeResolver,
                    reducerResolver, context, remoteInvoker, a2aInvoker, bearerToken, checkpointer,
                    executorId: resumeId);
            }
            else
            {
                executors[node.Id] = new GraphNodeExecutor(
                    node, manifest, registry, lifecycle,
                    predicateResolver, effectResolver, codeNodeResolver,
                    reducerResolver, context, remoteInvoker, a2aInvoker, bearerToken, checkpointer);
            }
        }

        if (!executors.TryGetValue(manifest.Entry, out var startExecutor))
        {
            throw new InvalidOperationException($"Start node '{manifest.Entry}' not found in graph '{manifest.Id}'.");
        }

        var builder = new WorkflowBuilder(ExecutorBindingExtensions.BindExecutor(startExecutor))
            .WithName(manifest.Id)
            .WithDescription(manifest.Description ?? $"Vais agent graph '{manifest.Id}' v{manifest.Version}.");

        // Manifest edges: remap From for interrupt nodes → resume router.
        foreach (var edge in manifest.Edges)
        {
            var fromId = interruptNodeIds.Contains(edge.From) ? $"{edge.From}_hitl_resume" : edge.From;
            if (!executors.TryGetValue(fromId, out var fromExec) ||
                !executors.TryGetValue(edge.To, out var toExec))
            {
                continue;
            }
            builder.AddEdge(
                ExecutorBindingExtensions.BindExecutor(fromExec),
                ExecutorBindingExtensions.BindExecutor(toExec));
        }

        // Structural HITL edges: emitter → port → resume router.
        foreach (var nodeId in interruptNodeIds)
        {
            var emitterBinding = ExecutorBindingExtensions.BindExecutor(executors[nodeId]);
            RequestPort<GraphMessage, GraphMessage> port = hitlPorts[nodeId];
            var resumeBinding = ExecutorBindingExtensions.BindExecutor(executors[$"{nodeId}_hitl_resume"]);
            builder.AddEdge(emitterBinding, port);    // implicit RequestPort → ExecutorBinding
            builder.AddEdge(port, resumeBinding);     // implicit RequestPort → ExecutorBinding
        }

        // WithOutputFrom: End nodes + resume routers (not emitters or ports).
        var outputBindings = executors.Values
            .Where(e => string.Equals(e.NodeKind, "End", StringComparison.Ordinal))
            .Select(e => ExecutorBindingExtensions.BindExecutor(e))
            .ToList<ExecutorBinding>();
        foreach (var nodeId in interruptNodeIds)
        {
            outputBindings.Add(ExecutorBindingExtensions.BindExecutor(executors[$"{nodeId}_hitl_resume"]));
        }
        if (outputBindings.Count > 0)
        {
            builder = builder.WithOutputFrom([.. outputBindings]);
        }

        return new MafGraphBuildResult(builder.Build(validateOrphans: false), portIdToNodeId);
    }
}
