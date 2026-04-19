// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Helpers for constructing <see cref="ITerminationCondition"/> instances — notably
/// the bridge from the legacy <see cref="TerminationPredicate"/> delegate to the
/// preferred interface. Lives in Core (not Abstractions) so it can reference the
/// delegate that already shipped there.
/// </summary>
public static class TerminationConditions
{
    /// <summary>Wrap a synchronous predicate as an <see cref="ITerminationCondition"/>.</summary>
    public static ITerminationCondition FromPredicate(TerminationPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new PredicateCondition(predicate);
    }

    private sealed class PredicateCondition(TerminationPredicate predicate) : ITerminationCondition
    {
        public ValueTask<bool> ShouldTerminateAsync(
            IReadOnlyList<OrchestrationStep> steps,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(predicate(steps));
    }
}
