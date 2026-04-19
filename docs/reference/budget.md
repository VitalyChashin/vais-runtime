# Reference: RunBudget

`RunBudget` limits what a single `AskAsync` / `StreamAsync` run can do. All fields are optional; unset means no limit on that dimension. `RunBudget.Unlimited` is the default.

## Fields

| Field | Type | Counts | Enforced at |
|---|---|---|---|
| `MaxTurns` | `int?` | LLM invocations (one per tool-call round + the final one) | top of each turn, before provider call |
| `MaxDuration` | `TimeSpan?` | wall-clock since run entry | top of each turn, before provider call |
| `MaxPromptTokens` | `int?` | sum of `CompletionResponse.PromptTokens` across all turns | after each provider call |
| `MaxCompletionTokens` | `int?` | sum of `CompletionResponse.CompletionTokens` across all turns | after each provider call |
| `MaxToolCalls` | `int?` | each `IToolCallDispatcher.DispatchAsync` | before each dispatch |

## What each field protects against

- **`MaxTurns`** — runaway tool-calling loops. A pathological agent asking for a tool, getting a result, asking for another, and another, indefinitely, tripped here.
- **`MaxDuration`** — wall-clock stall. Slow provider + slow tool + slow retry can rack up minutes before throwing.
- **`MaxPromptTokens`** — history that grows unbounded across tool iterations. Each round's prompt carries prior assistant+tool turns, so prompt tokens inflate fast.
- **`MaxCompletionTokens`** — verbose model output. Less common as a runaway source; useful for cost caps.
- **`MaxToolCalls`** — tool abuse. Separate from `MaxTurns` because a single turn can emit multiple parallel tool calls.

## Enforcement behaviour

On breach, `StatefulAiAgent` throws `AgentBudgetExceededException`:

```csharp
public sealed class AgentBudgetExceededException : Exception
{
    public string BudgetField { get; }   // e.g. "MaxTurns"
    public object Limit { get; }         // the configured limit
    public object Observed { get; }      // what the counter / stopwatch was at when breached
}
```

The exception path emits:
- `TurnFailed(At, Context, ErrorType = "AgentBudgetExceededException", ErrorMessage, Duration)`
- Usage sink sees `Succeeded = false`, `ErrorType = "AgentBudgetExceededException"`.

Budget breaches do NOT emit a `TurnCompleted`. Session is NOT updated with a final assistant turn.

## Counter semantics

- **Per-run**, not per-turn. All counters reset when a new `AskAsync` / `StreamAsync` is called; they don't persist across runs.
- **Tool-call dispatch counts across iterations.** A run that makes three tool calls in turn 1 and two in turn 2 has tripped `MaxToolCalls = 4` before the second tool in turn 2 dispatches.
- **Token counts sum across iterations.** A run that uses 500 prompt tokens in turn 1 and 800 in turn 2 has reached 1300 — relevant for `MaxPromptTokens` thresholds.

## Wiring

```csharp
using Vais.Agents;
using Vais.Agents.Core;

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        Budget = new RunBudget(
            MaxTurns: 5,
            MaxToolCalls: 10,
            MaxPromptTokens: 30_000,
            MaxCompletionTokens: 4_000,
            MaxDuration: TimeSpan.FromSeconds(60)),
    });

try
{
    var reply = await agent.AskAsync(input);
}
catch (AgentBudgetExceededException ex)
{
    Console.Error.WriteLine($"Budget {ex.BudgetField}: limit {ex.Limit}, observed {ex.Observed}");
}
```

## Streaming

All fields apply identically on `StreamAsync`. An in-progress streamed turn drains to whatever it had before the check fires — the consumer sees any deltas already yielded; the next iteration's check trips + throws.

## Common defaults

No strong consensus across the industry; reasonable starting points for an interactive chat agent:

- `MaxTurns = 10` — handles most multi-step tool flows.
- `MaxToolCalls = 20` — generous per-run budget.
- `MaxPromptTokens` — 2-3× your model's context window divided by turn count; e.g. for a 128k context and `MaxTurns = 10`, start at `MaxPromptTokens = 500_000` (summed across turns) as a sanity cap, not a cost cap.
- `MaxDuration = TimeSpan.FromSeconds(30-60)` for interactive UX.

Tighten aggressively in untrusted multi-tenant scenarios — the budget is your last line of defence against a malicious prompt steering the agent into a loop.

## Observability

Budget breach emits `TurnFailed` + usage record with `Succeeded = false`. The `ErrorType` on both is `"AgentBudgetExceededException"` — grep for this in telemetry to alert on runs that ran past budget.

## See also

- [Execution loop concept](../concepts/execution-loop.md)
- [Events reference](events.md) — `TurnFailed`.
