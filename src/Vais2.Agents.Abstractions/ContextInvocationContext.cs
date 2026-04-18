// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Inputs passed to an <see cref="IContextProvider"/> for a single turn. Providers
/// read this record and return a <see cref="ContextContribution"/>; they never
/// mutate the candidate request directly — mutations happen on the host side when
/// contributions are merged.
/// </summary>
/// <param name="Candidate">
/// The request as it stands immediately after the history reducer, before any
/// context provider has contributed. Providers inspect <see cref="CompletionRequest.History"/>
/// (the most-recent user turn is at the tail) and existing
/// <see cref="CompletionRequest.SystemPrompt"/> / <see cref="CompletionRequest.Tools"/>
/// to decide what to contribute.
/// </param>
/// <param name="AmbientContext">Ambient <see cref="AgentContext"/> for this turn (user, tenant, correlation id).</param>
/// <param name="Session">The session the agent is bound to. Non-null for every <c>StatefulAiAgent</c>-originated turn.</param>
public sealed record ContextInvocationContext(
    CompletionRequest Candidate,
    AgentContext AmbientContext,
    IAgentSession Session);
