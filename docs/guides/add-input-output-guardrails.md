# Guide: add input and output guardrails

Input guardrails gate what reaches the model. Output guardrails gate what reaches the caller. Both use the same `GuardrailOutcome` model; both fire per turn.

## Input — reject prompt injection attempts

```csharp
using Vais.Agents;

sealed class PromptInjectionGuardrail : IInputGuardrail
{
    private static readonly string[] BadPhrases = {
        "ignore previous instructions",
        "disregard the system prompt",
        "you are now",
    };

    public Task<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext ctx, CancellationToken ct)
    {
        var lastUser = request.History.LastOrDefault(t => t.Role == AgentChatRole.User)?.Text ?? "";
        foreach (var phrase in BadPhrases)
        {
            if (lastUser.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(GuardrailOutcome.Deny($"Blocked: '{phrase}'"));
        }
        return Task.FromResult(GuardrailOutcome.Pass);
    }
}
```

## Output — redact obvious PII

v0.4 doesn't support `Replacement` on `GuardrailOutcome` — Deny is the only blocking decision. If you need text rewriting, do it in an `IAgentFilter` (request/response pipeline) instead and use the output guardrail only for hard blocks.

```csharp
sealed class NoCreditCardsGuardrail : IOutputGuardrail
{
    private static readonly Regex CreditCard = new(@"\b(?:\d[ -]*?){13,19}\b");

    public Task<GuardrailOutcome> EvaluateAsync(CompletionResponse response, AgentContext ctx, CancellationToken ct)
        => CreditCard.IsMatch(response.Text)
            ? Task.FromResult(GuardrailOutcome.Deny("Response contained a credit-card-looking number."))
            : Task.FromResult(GuardrailOutcome.Pass);
}
```

## Wire both

```csharp
var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        InputGuardrails  = new IInputGuardrail[]  { new PromptInjectionGuardrail() },
        OutputGuardrails = new IOutputGuardrail[] { new NoCreditCardsGuardrail() },
    });

try
{
    var reply = await agent.AskAsync(userMessage);
}
catch (AgentGuardrailDeniedException ex)
{
    // ex.Layer = Input | Output; ex.Message carries the guardrail's reason.
    Console.Error.WriteLine($"Denied by {ex.Layer}: {ex.Message}");
}
```

## Tool guardrails — the third layer

Covered in [wire a custom tool](wire-a-custom-tool.md#5-guard-it). Tool guardrails fire around each tool dispatch; they can Pass, Deny, or Interrupt for HITL.

## Events on deny

When a guardrail denies:

- `GuardrailTriggered(At, Context, Layer, Decision = Deny, Reason)` — published first.
- `TurnFailed(At, Context, ErrorType = "AgentGuardrailDeniedException", ErrorMessage, Duration)` — published next.
- `AgentGuardrailDeniedException` — thrown from `AskAsync` / `StreamAsync`.

Subscribe to the bus for audit logging:

```csharp
bus.Subscribe((@event, ct) =>
{
    if (@event is GuardrailTriggered g)
        audit.Log(g.At, g.Context.UserId, g.Layer, g.Decision, g.Reason);
    return ValueTask.CompletedTask;
});
```

## Streaming semantics

- **Input guardrails** fire before each streamed turn starts — same as non-streaming.
- **Output guardrails** fire **after** the stream drains on the final (non-tool-call) turn. Deltas have already gone to the consumer; strict pre-emit gating is `AskAsync`'s job. Documented limitation.

If you need pre-emit gating, use `IStreamingAgentFilter.OnStreamDeltaAsync` to intercept each delta — that runs inline with the stream and can throw to abort, at the cost of having to buffer / check each fragment.

## Composing multiple guardrails per layer

Pass a list; guardrails run in order until one returns non-Pass:

```csharp
new StatefulAgentOptions
{
    InputGuardrails = new IInputGuardrail[]
    {
        new PromptInjectionGuardrail(),      // cheap local check first
        new ExternalModerationGuardrail(),   // expensive network call, only if earlier passed
        new TenantPolicyGuardrail(policies),
    }
}
```

## Things that catch people

- **Async failures propagate.** Exceptions thrown from `EvaluateAsync` fail the turn just like Deny — they surface as `TurnFailed` with the exception type name. If you want resilient network-backed guardrails, wrap them with retry / circuit-break inside your implementation.
- **Output guardrails don't see tool-call intermediate turns.** They fire only on the final turn's `CompletionResponse` after the tool loop settles. If you need to gate what a tool call's output looks like, use an `IToolGuardrail`.
- **`AgentContext`** is ambient (`IAgentContextAccessor.Current`) + options-overlaid. If you need request-scoped context (`UserId`, `TenantId`), set it via `AsyncLocalAgentContextAccessor.Push(...)` in your request handler.

## See also

- [Guardrails concept](../concepts/guardrails.md)
- [Execution loop concept](../concepts/execution-loop.md) — where each layer fires.
- [Events reference](../reference/events.md)
- Samples: `samples/InputOutputGuardrails/`, `samples/ToolGuardrailsAndInterrupt/` (per samples plan)
