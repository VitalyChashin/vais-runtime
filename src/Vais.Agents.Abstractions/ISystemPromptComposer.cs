// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Builds the base system prompt for a turn from one or more parts. When a composer
/// is supplied (see <c>StatefulAgentOptions.SystemPromptComposer</c>), the host
/// calls <see cref="ComposeAsync"/> per turn and uses the returned string as the
/// base system prompt — the plain <c>SystemPrompt</c> string is ignored in that case.
/// Context-provider <c>SystemPromptAddendum</c> values still concatenate on top.
/// </summary>
/// <remarks>
/// Typically paired with <see cref="ISystemPromptContributor"/> via the shipped
/// <c>AggregatingSystemPromptComposer</c>, which orders contributors by priority
/// and joins their outputs. Custom composers can ignore contributors entirely and
/// build the prompt however they like.
/// </remarks>
public interface ISystemPromptComposer
{
    /// <summary>
    /// Return the composed system prompt for this turn, or null to produce no base
    /// prompt. Return values are used verbatim — callers supply their own leading
    /// or trailing whitespace if they want any.
    /// </summary>
    ValueTask<string?> ComposeAsync(AgentContext context, CancellationToken cancellationToken = default);
}
