// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Structural validation for <see cref="AgentGraphManifest"/>. Accumulates every
/// violation found in one pass (same convention as
/// <see cref="JsonAgentManifestLoader"/>). Invoked by
/// <see cref="JsonAgentGraphManifestLoader"/> after parsing; can also be called
/// standalone by consumers that build manifests programmatically.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation rules</b>:
/// </para>
/// <list type="bullet">
///   <item><description>Entry node id must exist in <see cref="AgentGraphManifest.Nodes"/>.</description></item>
///   <item><description>Node ids must be unique within the manifest.</description></item>
///   <item><description>Every <see cref="GraphEdge.From"/> and <see cref="GraphEdge.To"/> must reference an existing node id.</description></item>
///   <item><description>No outgoing edges from <c>End</c>-kind nodes (terminal).</description></item>
///   <item><description>No self-loop on <c>End</c>-kind (doesn't make sense semantically).</description></item>
///   <item><description>Agent-kind nodes require <see cref="GraphNode.Ref"/>.</description></item>
///   <item><description>Code-kind nodes require <see cref="GraphNode.HandlerRef"/>.</description></item>
///   <item><description><see cref="GraphHandlerRef.TypeName"/> must be non-empty and free of whitespace wherever it appears.</description></item>
///   <item><description>Cycles are permitted only when a <see cref="AgentGraphManifest.MaxSteps"/> guard is set.</description></item>
///   <item><description><see cref="GraphStateBindings"/> input/output keys must be declared in <c>spec.state.schema.properties</c> when a schema is present (well-known runtime keys <c>messages</c> and <c>lastAssistantText</c> are always exempt).</description></item>
///   <item><description><see cref="AgentGraphManifest.StateReducers"/> keys must be declared in <c>spec.state.schema.properties</c> when a schema is present.</description></item>
///   <item><description>A node with any <see cref="GraphEdge.Concurrent"/> outgoing edge must use <c>concurrent: true</c> on all its outgoing edges — mixing routing and fan-out on one node is ambiguous.</description></item>
///   <item><description>Concurrent branches from the same fork source must declare non-overlapping <c>stateBindings.output</c> keys to avoid non-deterministic state merges at the join (well-known keys <c>messages</c> and <c>lastAssistantText</c> are exempt).</description></item>
///   <item><description>A node that is both a join target (2+ concurrent incoming edges) and a fork source (concurrent outgoing edges) is rejected — nested fan-out is not supported.</description></item>
/// </list>
/// </remarks>
public static class AgentGraphManifestValidator
{
    private const string EndKind = "End";
    private const string AgentKind = "Agent";
    private const string CodeKind = "Code";
    private const string InterruptKind = "Interrupt";

    // Well-known keys the runtime writes unconditionally — exempt from stateBindings schema cross-check.
    private static readonly HashSet<string> WellKnownStateKeys = new(StringComparer.Ordinal)
    {
        "messages",
        "lastAssistantText",
    };

    /// <summary>Validate <paramref name="manifest"/>, appending any issues to <paramref name="errors"/>.</summary>
    /// <param name="manifest">Manifest to validate.</param>
    /// <param name="errors">Error sink. Each violation appended as one string.</param>
    /// <param name="prefix">Optional prefix for every error line (e.g. file + document index in multi-doc streams).</param>
    public static void Validate(AgentGraphManifest manifest, List<string> errors, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(errors);

        var nodeById = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var node in manifest.Nodes)
        {
            if (nodeById.ContainsKey(node.Id))
            {
                errors.Add($"{prefix}duplicate node id '{node.Id}'");
            }
            else
            {
                nodeById[node.Id] = node;
            }

            ValidateNodeKind(node, errors, prefix);
        }

        if (!nodeById.ContainsKey(manifest.Entry))
        {
            errors.Add($"{prefix}entry node '{manifest.Entry}' not found in spec.nodes");
        }

        foreach (var edge in manifest.Edges)
        {
            if (!nodeById.TryGetValue(edge.From, out var fromNode))
            {
                errors.Add($"{prefix}edge references unknown 'from' node '{edge.From}'");
            }
            else if (string.Equals(fromNode.Kind, EndKind, StringComparison.Ordinal))
            {
                errors.Add($"{prefix}edge has 'from' = End-kind node '{edge.From}' — End is terminal");
            }

            if (!nodeById.ContainsKey(edge.To))
            {
                errors.Add($"{prefix}edge references unknown 'to' node '{edge.To}'");
            }

            if (edge.When is not null)
            {
                ValidateEdgePredicate(edge.When, $"edge '{edge.From}'→'{edge.To}'", errors, prefix);
            }

            if (edge.OnTraverse is GraphEdgeEffect.HandlerRef effectHr)
            {
                ValidateHandlerRef(effectHr.Handler, $"edge '{edge.From}'→'{edge.To}' effect handlerRef", errors, prefix);
            }
        }

        if (HasCycle(manifest, nodeById) && manifest.MaxSteps is null)
        {
            errors.Add($"{prefix}graph contains a cycle but spec.maxSteps is unset — add a ceiling or remove the back-edge");
        }

        ValidateStateBindings(manifest, errors, prefix);
        ValidateStateReducers(manifest, errors, prefix);
        ValidateConcurrentEdges(manifest, nodeById, errors, prefix);
    }

    /// <summary>
    /// Cross-checks <c>stateBindings.output</c> keys on Agent-kind nodes against the referenced
    /// agent's <see cref="AgentManifest.OutputSchema"/>. Agents not in <paramref name="resolveAgent"/>,
    /// remote refs, and agents without an <c>OutputSchema</c> are silently skipped.
    /// </summary>
    /// <param name="manifest">Graph manifest to check.</param>
    /// <param name="resolveAgent">Resolver: given (agentId, version?) returns the manifest or null if unknown.</param>
    /// <param name="errors">Error sink.</param>
    /// <param name="prefix">Optional prefix for every error line.</param>
    public static void ValidateAgentOutputSchemaBindings(
        AgentGraphManifest manifest,
        Func<string, string?, AgentManifest?> resolveAgent,
        List<string> errors,
        string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(resolveAgent);
        ArgumentNullException.ThrowIfNull(errors);

        foreach (var node in manifest.Nodes)
        {
            if (!string.Equals(node.Kind, AgentKind, StringComparison.Ordinal)) continue;
            if (node.Ref is null) continue;
            if (node.StateBindings?.Output is not { Count: > 0 } outputs) continue;

            // Remote agents run outside this runtime — their OutputSchema is not available at load time.
            if (node.Ref.RuntimeUrl is not null || node.Ref.A2AUrl is not null) continue;

            var agentManifest = resolveAgent(node.Ref.Id, node.Ref.Version);
            if (agentManifest is null) continue;

            if (agentManifest.OutputSchema is not { } schemaEl ||
                schemaEl.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;
            if (!schemaEl.TryGetProperty("properties", out var propsEl) ||
                propsEl.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            foreach (var key in outputs)
            {
                if (!WellKnownStateKeys.Contains(key) && !propsEl.TryGetProperty(key, out _))
                {
                    errors.Add($"{prefix}node '{node.Id}' stateBindings.output key '{key}' not found in agent '{node.Ref.Id}' OutputSchema.properties");
                }
            }
        }
    }

    private static void ValidateHandlerRef(GraphHandlerRef handlerRef, string context, List<string> errors, string prefix)
    {
        if (string.IsNullOrEmpty(handlerRef.TypeName) || handlerRef.TypeName.Any(char.IsWhiteSpace))
        {
            errors.Add($"{prefix}{context} typeName '{handlerRef.TypeName}' is invalid — must be a non-empty fully-qualified .NET type name with no whitespace");
        }
    }

    private static void ValidateEdgePredicate(GraphEdgePredicate predicate, string context, List<string> errors, string prefix)
    {
        switch (predicate)
        {
            case GraphEdgePredicate.HandlerRef hr:
                ValidateHandlerRef(hr.Handler, $"{context} predicate handlerRef", errors, prefix);
                break;
            case GraphEdgePredicate.AllOf allOf:
                foreach (var p in allOf.Predicates)
                    ValidateEdgePredicate(p, context, errors, prefix);
                break;
            case GraphEdgePredicate.AnyOf anyOf:
                foreach (var p in anyOf.Predicates)
                    ValidateEdgePredicate(p, context, errors, prefix);
                break;
            case GraphEdgePredicate.Not not:
                ValidateEdgePredicate(not.Predicate, context, errors, prefix);
                break;
        }
    }

    private static void ValidateStateBindings(AgentGraphManifest manifest, List<string> errors, string prefix)
    {
        if (manifest.StateSchema is not { } schemaEl || schemaEl.ValueKind != System.Text.Json.JsonValueKind.Object)
            return;
        if (!schemaEl.TryGetProperty("properties", out var propsEl) || propsEl.ValueKind != System.Text.Json.JsonValueKind.Object)
            return;

        foreach (var node in manifest.Nodes)
        {
            if (node.StateBindings is not { } bindings) continue;

            if (bindings.Input is { } inputs)
            {
                foreach (var key in inputs)
                {
                    if (!WellKnownStateKeys.Contains(key) && !propsEl.TryGetProperty(key, out _))
                        errors.Add($"{prefix}node '{node.Id}' stateBindings.input key '{key}' is not declared in spec.state.schema.properties");
                }
            }

            if (bindings.Output is { } outputs)
            {
                foreach (var key in outputs)
                {
                    if (!WellKnownStateKeys.Contains(key) && !propsEl.TryGetProperty(key, out _))
                        errors.Add($"{prefix}node '{node.Id}' stateBindings.output key '{key}' is not declared in spec.state.schema.properties");
                }
            }
        }
    }

    private static void ValidateStateReducers(AgentGraphManifest manifest, List<string> errors, string prefix)
    {
        if (manifest.StateReducers is not { Count: > 0 } reducers)
            return;

        // HandlerRef TypeName check runs regardless of whether a StateSchema is present.
        foreach (var (key, reducer) in reducers)
        {
            if (reducer is GraphStateReducer.HandlerRef hrReducer)
            {
                ValidateHandlerRef(hrReducer.Handler, $"stateReducers['{key}'] handlerRef", errors, prefix);
            }
        }

        // Key ↔ schema cross-check only runs when a schema is declared.
        if (manifest.StateSchema is not { } schemaEl || schemaEl.ValueKind != System.Text.Json.JsonValueKind.Object)
            return;
        if (!schemaEl.TryGetProperty("properties", out var propsEl) || propsEl.ValueKind != System.Text.Json.JsonValueKind.Object)
            return;

        foreach (var key in reducers.Keys)
        {
            if (!propsEl.TryGetProperty(key, out _))
            {
                errors.Add($"{prefix}stateReducers key '{key}' is not declared in spec.state.schema.properties");
            }
        }
    }

    private static void ValidateNodeKind(GraphNode node, List<string> errors, string prefix)
    {
        switch (node.Kind)
        {
            case AgentKind:
                if (node.Ref is null)
                {
                    errors.Add($"{prefix}Agent-kind node '{node.Id}' has no ref");
                }
                break;
            case CodeKind:
                if (node.HandlerRef is null)
                {
                    errors.Add($"{prefix}Code-kind node '{node.Id}' has no handlerRef");
                }
                else
                {
                    ValidateHandlerRef(node.HandlerRef, $"Code-kind node '{node.Id}' handlerRef", errors, prefix);
                }
                break;
            case InterruptKind:
            case EndKind:
                // No additional requirements.
                break;
            default:
                errors.Add($"{prefix}node '{node.Id}' has unknown kind '{node.Kind}' (expected Agent | Code | Interrupt | End)");
                break;
        }
    }

    private static void ValidateConcurrentEdges(
        AgentGraphManifest manifest,
        Dictionary<string, GraphNode> nodeById,
        List<string> errors,
        string prefix)
    {
        var concurrentEdges = manifest.Edges.Where(e => e.Concurrent).ToList();
        if (concurrentEdges.Count == 0) return;

        // Rule 1: a node with any concurrent edge must not also have non-concurrent edges.
        var concurrentSources = new HashSet<string>(
            concurrentEdges.Select(e => e.From), StringComparer.Ordinal);
        foreach (var edge in manifest.Edges)
        {
            if (!edge.Concurrent && concurrentSources.Contains(edge.From))
            {
                errors.Add($"{prefix}node '{edge.From}' has both concurrent and non-concurrent outgoing edges — " +
                    "a fork node must use concurrent edges exclusively");
                concurrentSources.Remove(edge.From); // report once per node
            }
        }

        // Rule 2: non-overlapping output bindings across branches from the same fork source.
        var fanOutGroups = concurrentEdges.GroupBy(e => e.From);
        foreach (var group in fanOutGroups)
        {
            var branchIds = group.Select(e => e.To).ToList();
            if (branchIds.Count < 2) continue;
            var seenOutputKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var branchId in branchIds)
            {
                if (!nodeById.TryGetValue(branchId, out var branchNode)) continue;
                var outputKeys = branchNode.StateBindings?.Output ?? Array.Empty<string>();
                foreach (var key in outputKeys)
                {
                    if (WellKnownStateKeys.Contains(key)) continue;
                    if (seenOutputKeys.TryGetValue(key, out var firstBranch))
                    {
                        errors.Add($"{prefix}concurrent branches '{firstBranch}' and '{branchId}' " +
                            $"both declare stateBindings.output key '{key}' — output bindings must be " +
                            "non-overlapping across concurrent branches to avoid non-deterministic state merges");
                    }
                    else
                    {
                        seenOutputKeys[key] = branchId;
                    }
                }
            }
        }

        // Rule 3: a join target (2+ concurrent incoming edges) must not also be a fork source.
        var joinTargets = new HashSet<string>(
            concurrentEdges.GroupBy(e => e.To)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key),
            StringComparer.Ordinal);
        var forkSources = new HashSet<string>(
            concurrentEdges.GroupBy(e => e.From)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key),
            StringComparer.Ordinal);
        foreach (var target in joinTargets)
        {
            if (forkSources.Contains(target))
            {
                errors.Add($"{prefix}node '{target}' is both a join target and a fork source — " +
                    "nested fan-out is not supported");
            }
        }
    }

    /// <summary>DFS-based cycle detection. White/grey/black colouring.</summary>
    private static bool HasCycle(AgentGraphManifest manifest, Dictionary<string, GraphNode> nodeById)
    {
        // Adjacency list built from edges.
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in manifest.Edges)
        {
            if (!adj.TryGetValue(edge.From, out var list))
            {
                list = new List<string>();
                adj[edge.From] = list;
            }
            list.Add(edge.To);
        }

        // 0 = unvisited, 1 = on stack, 2 = done.
        var colour = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in nodeById.Keys) colour[id] = 0;

        var stack = new Stack<(string Node, int Next)>();
        foreach (var start in nodeById.Keys)
        {
            if (colour[start] != 0) continue;
            stack.Clear();
            stack.Push((start, 0));
            colour[start] = 1;
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                if (!adj.TryGetValue(node, out var children) || next >= children.Count)
                {
                    colour[node] = 2;
                    continue;
                }
                stack.Push((node, next + 1));
                var child = children[next];
                if (!colour.TryGetValue(child, out var childColour))
                {
                    // Unknown node — reported separately by edge-endpoint check.
                    continue;
                }
                if (childColour == 1)
                {
                    return true;
                }
                if (childColour == 0)
                {
                    stack.Push((child, 0));
                    colour[child] = 1;
                }
            }
        }
        return false;
    }
}
