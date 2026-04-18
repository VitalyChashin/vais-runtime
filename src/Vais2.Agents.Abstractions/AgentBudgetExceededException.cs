// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Thrown by <c>StatefulAiAgent</c> when a <see cref="RunBudget"/> cap is
/// exceeded in the outer tool-call loop. Carries the specific field that was
/// exceeded along with the limit and the observed value — both for the caller
/// handling the exception and for telemetry (usage sink records the type name,
/// event bus sees <c>TurnFailed</c> with a descriptive message).
/// </summary>
public sealed class AgentBudgetExceededException : Exception
{
    /// <summary>Which <see cref="RunBudget"/> field was exceeded (e.g., <c>"MaxTurns"</c>).</summary>
    public string BudgetField { get; }

    /// <summary>The configured limit for <see cref="BudgetField"/>.</summary>
    public object Limit { get; }

    /// <summary>The observed value that exceeded the limit.</summary>
    public object Observed { get; }

    /// <summary>Construct an exception carrying the triggering field, limit, and observed value.</summary>
    public AgentBudgetExceededException(string budgetField, object limit, object observed)
        : base($"RunBudget.{budgetField} exceeded: limit={limit}, observed={observed}")
    {
        BudgetField = budgetField;
        Limit = limit;
        Observed = observed;
    }
}
