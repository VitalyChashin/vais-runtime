// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Composable per-turn context contributor. Each provider in the configured chain
/// runs once per turn, inspects the <see cref="ContextInvocationContext.Candidate"/>,
/// and returns a <see cref="ContextContribution"/> that the host merges into the
/// request before it reaches the model.
/// </summary>
/// <remarks>
/// <para>
/// Providers should be side-effect-light. Hosts invoke them synchronously in order
/// on the turn's critical path; a slow provider slows every turn.
/// </para>
/// <para>
/// Exceptions propagate — a thrown provider fails the whole turn. Context is
/// load-bearing; silent swallow would mask missing retrieval results or guardrail
/// metadata. Consumers who want swallow semantics should wrap with a
/// resilience-handling provider.
/// </para>
/// </remarks>
public interface IContextProvider
{
    /// <summary>Contribute to the turn. Returning <see cref="ContextContribution.Empty"/> is a valid no-op.</summary>
    ValueTask<ContextContribution> InvokeAsync(
        ContextInvocationContext context,
        CancellationToken cancellationToken = default);
}
