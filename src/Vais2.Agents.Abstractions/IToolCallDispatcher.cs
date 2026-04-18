// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// The seam <c>StatefulAiAgent</c>'s outer loop uses to invoke each tool the
/// model requests. Implementations resolve the tool, enforce tool-level guardrails,
/// and translate exceptions into structured <see cref="ToolCallOutcome"/>s.
/// </summary>
/// <remarks>
/// <para>
/// This is the future anchor for durable execution — a <c>JournaledToolCallDispatcher</c>
/// wrapping any inner dispatcher can record each (<see cref="ToolCallRequest"/>,
/// <see cref="ToolCallOutcome"/>) pair for replay. The interface is shaped to
/// accept that layering without further change.
/// </para>
/// <para>
/// Dispatcher exceptions that escape (e.g., a guardrail denial, a dispatcher that
/// cannot resolve the tool) abort the whole turn. Exceptions from the tool itself
/// are expected to be caught by the dispatcher and returned as
/// <see cref="ToolCallOutcome"/> with a non-null <see cref="ToolCallOutcome.Error"/>
/// — the loop feeds them back to the model rather than failing the turn.
/// </para>
/// </remarks>
public interface IToolCallDispatcher
{
    /// <summary>Dispatch a single tool call. Returns the outcome (success or recoverable error).</summary>
    ValueTask<ToolCallOutcome> DispatchAsync(
        ToolCallRequest request,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
