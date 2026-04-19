# BudgetEnforcement

Trips each `RunBudget` dimension — `MaxToolCalls`, `MaxTurns`, `MaxCompletionTokens` — and prints the `AgentBudgetExceededException` field + limit + actual. Plus a clean-path scenario for reference.

**Concepts:** [execution loop](../../docs/concepts/execution-loop.md).
**Reference:** [`RunBudget`](../../docs/reference/budget.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`.
**Needs API key:** no.

```bash
dotnet run --project samples/BudgetEnforcement
```
