// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Composable, async termination check for multi-agent orchestrators. Consulted
/// after each <see cref="OrchestrationStep"/> to decide whether the run should end.
/// </summary>
/// <remarks>
/// <para>
/// Preferred over the older <c>TerminationPredicate</c> delegate (in Core) for new
/// code — the interface supports async checks (e.g., querying an external approval
/// service) and composes cleanly via wrappers. The delegate shipped earlier and
/// remains for back-compat; use <c>Vais2.Agents.Core.TerminationConditions.FromPredicate</c>
/// to bridge.
/// </para>
/// </remarks>
public interface ITerminationCondition
{
    /// <summary>Return true to terminate the orchestrator run after the supplied step(s).</summary>
    ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<OrchestrationStep> steps,
        CancellationToken cancellationToken = default);
}
