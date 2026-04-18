// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// One participant in a multi-agent orchestration. Pairs a stable name (used to
/// label turns in the shared conversation and in emitted
/// <see cref="OrchestrationStep"/>s) with the <see cref="ICompletionProvider"/>
/// that will produce its replies and an optional system prompt.
/// </summary>
/// <remarks>
/// <para>
/// Orchestrators drive providers directly rather than going through
/// <see cref="IAiAgent"/>, because <see cref="IAiAgent"/> owns per-agent history
/// and a multi-agent conversation has a <em>shared</em> history. Mixing the two
/// would either fragment the shared view into each agent's private one or
/// corrupt the agent's own conversation state with turns it didn't author.
/// Participants are intentionally a lower-level surface than agents.
/// </para>
/// </remarks>
/// <param name="Name">Human-readable identifier, used to tag this participant's turns.</param>
/// <param name="Provider">The completion provider that produces this participant's replies.</param>
/// <param name="SystemPrompt">Optional per-participant system instruction.</param>
public sealed record AgentParticipant(
    string Name,
    ICompletionProvider Provider,
    string? SystemPrompt = null);
