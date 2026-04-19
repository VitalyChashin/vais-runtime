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
///   <item><description>Cycles are permitted only when a <see cref="AgentGraphManifest.MaxSteps"/> guard is set.</description></item>
/// </list>
/// </remarks>
public static class AgentGraphManifestValidator
{
    private const string EndKind = "End";
    private const string AgentKind = "Agent";
    private const string CodeKind = "Code";
    private const string InterruptKind = "Interrupt";

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
        }

        if (HasCycle(manifest, nodeById) && manifest.MaxSteps is null)
        {
            errors.Add($"{prefix}graph contains a cycle but spec.maxSteps is unset — add a ceiling or remove the back-edge");
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
