// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// One part of a multi-part system prompt. Contributors are aggregated by an
/// <see cref="ISystemPromptComposer"/> (the shipped <c>AggregatingSystemPromptComposer</c>
/// orders by <see cref="Priority"/> ascending and joins non-null / non-empty outputs
/// with <c>"\n\n"</c>).
/// </summary>
public interface ISystemPromptContributor
{
    /// <summary>
    /// Ordering hint. Lower values run earlier — contributor with <c>Priority = 0</c>
    /// appears at the top of the composed prompt. Matches ASP.NET middleware ordering.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Return this contributor's slice of the prompt, or null/empty to contribute nothing.
    /// </summary>
    ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default);
}
