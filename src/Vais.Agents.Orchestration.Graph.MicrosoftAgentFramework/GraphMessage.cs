// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;

namespace Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

/// <summary>
/// Wire-level message passed between MAF executors in a Vais graph run. Carries the
/// full graph state snapshot plus super-step + run metadata so each executor can
/// evaluate outgoing edges + enforce the <see cref="AgentGraphManifest.MaxSteps"/>
/// ceiling without touching MAF's shared-state API.
/// </summary>
/// <remarks>
/// <para>
/// The state dictionary is intentionally mutable so node executors can mutate it
/// in place — the MAF super-step boundary hands off a fresh snapshot on each edge
/// traversal via <c>with</c> expression anyway, so cross-executor sharing is
/// never accidental.
/// </para>
/// </remarks>
/// <param name="State">Graph state at this super-step boundary.</param>
/// <param name="SuperStep">Zero-based super-step count since run start.</param>
/// <param name="RunId">Graph-run correlation id. Matches <see cref="AgentGraphEvent.RunId"/>.</param>
/// <param name="MaxSteps">Resolved max-step ceiling (from manifest or default).</param>
/// <param name="SourceNodeId">Id of the node whose executor produced this message (null on the initial input).</param>
public sealed record GraphMessage(
    IDictionary<string, JsonElement> State,
    int SuperStep,
    string RunId,
    int MaxSteps,
    string? SourceNodeId = null)
{
    /// <summary>
    /// When non-null, the executor for this node id skips its body on the first invocation and
    /// jumps directly to outgoing-edge evaluation — the MAF equivalent of
    /// <c>InProcessGraphOrchestrator</c>'s <c>skipNodeBody</c> resume flag. Cleared (set to
    /// null) on every outgoing <see cref="GraphMessage"/> so only the targeted executor skips.
    /// </summary>
    public string? ResumeFromNodeId { get; init; }

    /// <summary>
    /// OTEL <see cref="ActivityContext"/> of the <c>graph.fanout</c> span opened by the fork
    /// source. Branch executors use this as parent for their <c>graph.node</c> span so Langfuse
    /// renders concurrent branches nested under the fanout marker. Cleared by
    /// <see cref="GraphJoinNodeExecutor"/> before delegating to the base body so the join node
    /// and all downstream nodes are parented to the root <c>graph.run</c> span instead.
    /// </summary>
    internal ActivityContext? FanoutContext { get; init; }
}

/// <summary>
/// MAF workflow event surfaced after an agent-kind node invocation completes.
/// Carries the resolved input text and the agent's response text so the run store
/// can persist them without requiring a separate agent-event-bus correlation.
/// </summary>
internal sealed class NodeAgentInvokedEvent : WorkflowEvent
{
    public NodeAgentInvokedEvent(string nodeId, string agentId, string inputText, string outputText, int inputTokens, int outputTokens)
        : base(data: null)
    {
        NodeId = nodeId;
        AgentId = agentId;
        InputText = inputText;
        OutputText = outputText;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    public string NodeId { get; }
    public string AgentId { get; }
    public string InputText { get; }
    public string OutputText { get; }
    public int InputTokens { get; }
    public int OutputTokens { get; }
}

/// <summary>MAF workflow event surfaced by a graph node executor when it traverses an outgoing edge.</summary>
internal sealed class EdgeTraversedEvent : WorkflowEvent
{
    public EdgeTraversedEvent(string from, string to) : base(data: null)
    {
        From = from;
        To = to;
    }

    public string From { get; }

    public string To { get; }
}

/// <summary>MAF workflow event surfaced when an edge's <see cref="GraphEdgeEffect"/> mutates state.</summary>
internal sealed class StateUpdatedEvent : WorkflowEvent
{
    public StateUpdatedEvent(IReadOnlyList<string> changedKeys) : base(data: null)
    {
        ChangedKeys = changedKeys;
    }

    public IReadOnlyList<string> ChangedKeys { get; }
}

/// <summary>MAF workflow event surfaced when a graph hits an Interrupt-kind node.</summary>
internal sealed class GraphInterruptedEvent : WorkflowEvent
{
    public GraphInterruptedEvent(string nodeId, string interruptId, string? reason) : base(data: null)
    {
        NodeId = nodeId;
        InterruptId = interruptId;
        Reason = reason;
    }

    public string NodeId { get; }

    public string InterruptId { get; }

    public string? Reason { get; }
}
