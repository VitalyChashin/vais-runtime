// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Invocation mode for a <see cref="LocalAgentRef"/>. Determines whether the coordinator
/// waits for the sub-agent's result (<see cref="Blocking"/>) or fires and monitors it
/// asynchronously (<see cref="Background"/>).
/// </summary>
public enum LocalAgentInvocationMode
{
    /// <summary>
    /// The coordinator's LLM calls the sub-agent as a synchronous tool and waits for
    /// the result before continuing its turn. Default.
    /// </summary>
    Blocking = 0,

    /// <summary>
    /// The sub-agent run is dispatched asynchronously. The tool call returns a durable
    /// handle immediately; the coordinator can poll or cancel via management tools.
    /// Requires <c>IBackgroundAgentTracker</c> to be registered.
    /// </summary>
    Background = 1,
}

/// <summary>
/// Declarative reference to a local (same-runtime) agent that the coordinator can
/// invoke as a tool. When a <see cref="ToolRef.Source"/> matches
/// <c>"agent:&lt;name&gt;"</c> the runtime wraps the target agent as an
/// <see cref="ITool"/> via <c>LocalAgentTool</c> (blocking) or
/// <c>BackgroundLocalAgentTool</c> (background). Ships with v0.18 — closes P7
/// ("agent-as-tool over peer A2A is the default").
/// </summary>
/// <param name="Name">
/// Stable name for this binding — referenced from
/// <c>ToolRef.Source = "agent:&lt;Name&gt;"</c>. Unique within a manifest.
/// </param>
/// <param name="AgentId">
/// Target agent id in the registry. Defaults to <paramref name="Name"/> when null.
/// </param>
/// <param name="AgentVersion">
/// Optional pinned version. Null = latest lexicographic version.
/// </param>
/// <param name="Mode">
/// Blocking (default) or Background invocation mode.
/// </param>
public sealed record LocalAgentRef(
    string Name,
    string? AgentId = null,
    string? AgentVersion = null,
    LocalAgentInvocationMode Mode = LocalAgentInvocationMode.Blocking)
{
    /// <summary>
    /// Optional description override. When null the runtime derives the description
    /// from the target agent's manifest <c>Description</c> field.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Allow the coordinator LLM to supply a <c>sessionId</c> argument on each
    /// call, enabling multi-turn sub-conversations with the same child agent.
    /// When false (default) the session id is derived deterministically from the
    /// parent run id and call arguments so each distinct sub-task gets an isolated
    /// session that is cleaned up after the call.
    /// </summary>
    public bool AllowCallerSuppliedSession { get; init; } = false;

    /// <summary>
    /// Propagate the caller's <see cref="AgentContext.AllowedTools"/> to the child
    /// agent's context. When true (default) the child is constrained to the same
    /// tool set as the caller. Set false to let the child run under its own
    /// manifest's full tool set.
    /// </summary>
    public bool PropagateAllowedTools { get; init; } = true;
}
