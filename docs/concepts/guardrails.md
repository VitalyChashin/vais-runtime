# Guardrails

Three layers, one decision model. **Input** guardrails fire before the model is called; **output** guardrails fire on the model's response; **tool** guardrails fire around each dispatched tool call. All three return the same `GuardrailOutcome` enum — `Pass`, `Deny`, `Interrupt`.

## Why three layers

Each layer sees different context and has different failure modes:

- **Input** — the built `CompletionRequest` right before `ICompletionProvider.CompleteAsync`. Sees the full prompt + history + tool advertisement. Good for "reject prompt injection attempts" / "block PII in user input".
- **Output** — the `CompletionResponse` right after the provider returns, before the final assistant turn is appended. Sees the model's reply. Good for "strip PII from model output" / "block policy violations".
- **Tool** — the `ITool` plus its `JsonElement` arguments right before `ITool.InvokeAsync`. Sees tool name + arguments. Good for "require approval before deleting" / "block destructive tools in read-only contexts".

Collapsing into one layer loses signal; scattering across filters loses uniformity. Three layers + one outcome shape is the MAF-derived design.

## Core types

```csharp
namespace Vais.Agents;

public enum GuardrailDecision { Pass = 0, Deny = 1, Interrupt = 2 }

public enum GuardrailLayer { Input, Output, Tool }

public sealed record GuardrailOutcome(GuardrailDecision Decision, string? Reason = null, AgentInterrupt? InterruptPayload = null)
{
    public static readonly GuardrailOutcome Pass = new(GuardrailDecision.Pass);
    public static GuardrailOutcome Deny(string? reason = null) => new(GuardrailDecision.Deny, reason);
    public static GuardrailOutcome Interrupt(AgentInterrupt payload, string? reason = null)
        => new(GuardrailDecision.Interrupt, reason, payload);
}

public interface IInputGuardrail  { ValueTask<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext context, CancellationToken cancellationToken = default); }
public interface IOutputGuardrail { ValueTask<GuardrailOutcome> EvaluateAsync(CompletionResponse response, AgentContext context, CancellationToken cancellationToken = default); }
public interface IToolGuardrail   { ValueTask<GuardrailOutcome> BeforeInvokeAsync(ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default); }

public sealed class AgentGuardrailDeniedException : Exception
{
    public GuardrailLayer Layer { get; }
    public AgentGuardrailDeniedException(GuardrailLayer layer, string? reason);
}
```

## Decision model

| Decision | Effect |
|---|---|
| **Pass** | Continue. |
| **Deny** | Throw `AgentGuardrailDeniedException(layer, reason)`. Emit `GuardrailTriggered` event + `TurnFailed` event. Usage sink sees `Succeeded = false`. |
| **Interrupt** | Emit `InterruptRaised` event. Throw `AgentInterruptedException(payload)`. Caller handles HITL, supplies a `ResumeInput`, calls `ResumeAsync`. |

Multiple guardrails per layer run in sequence until one returns non-Pass or all pass.

## Wiring

```csharp
var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        InputGuardrails = new IInputGuardrail[]
        {
            new PromptInjectionGuardrail(),
            new PiiInInputGuardrail(),
        },
        OutputGuardrails = new IOutputGuardrail[]
        {
            new PiiInOutputGuardrail(),
        },
        ToolGuardrails = new IToolGuardrail[]
        {
            new DestructiveToolApprovalGuardrail(),
        },
    });
```

All three lists default empty. `StatefulAiAgent` runs input guardrails every turn (before the provider call, before filters); output guardrails at the end of the final (non-tool-call) turn. `IToolGuardrail.BeforeInvokeAsync` runs inside `DefaultToolCallDispatcher` before each `ITool.InvokeAsync`.

## A custom guardrail

```csharp
sealed class DestructiveToolApprovalGuardrail : IToolGuardrail
{
    public ValueTask<GuardrailOutcome> BeforeInvokeAsync(ITool tool, JsonElement arguments, AgentContext ctx, CancellationToken ct)
    {
        if (!tool.Name.StartsWith("delete_", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(GuardrailOutcome.Pass);

        // Require HITL approval before deleting anything.
        var interrupt = new AgentInterrupt(
            InterruptId: Guid.NewGuid().ToString("N"),
            Reason: $"Approval required to run destructive tool '{tool.Name}'.",
            Payload: arguments);
        return ValueTask.FromResult(GuardrailOutcome.Interrupt(interrupt, reason: "destructive-tool-approval"));
    }
}
```

When this fires, the agent throws `AgentInterruptedException(interrupt)`. The caller catches it, gathers a human decision, constructs a `ResumeInput`, and calls `agent.ResumeAsync(resumeInput)`.

## Streaming semantics

- **Input guardrails**: same as non-streaming — fire before the stream opens on every streamed turn.
- **Output guardrails**: fire **after** the stream drains on the final (non-tool-call) turn. Post-facto by design — deltas have already been yielded to the consumer. Strict pre-emit gating is `AskAsync`'s job; consumers needing it don't stream.
- **Tool guardrails**: fire inside the dispatcher exactly like non-streaming.

## Observability

- `GuardrailTriggered` event published on Deny + Interrupt. Carries `Layer`, `Decision`, optional `Reason`.
- `InterruptRaised` event published on Interrupt (additional to `GuardrailTriggered` — the two fire as a pair).
- `TurnFailed` event published when a Deny propagates out of the run.

## Extension points

- **Multiple guardrails per layer** — all run in order; first non-Pass wins.
- **Async** — guardrails can hit the network (external moderation API, policy store). Cancellation propagates.
- **Shared state** — guardrails have no per-run contract with each other. If you need one to know about another, share state via DI.

## Limitations / known gaps

- **No `Replacement` field on `GuardrailOutcome`.** The MAF spec includes a replacement-text field on output guardrails; we dropped it for v0.4 because it's layer-specific (makes no sense on input) and adds a branch to every call site. Revisit when a consumer actually wants it.
- **No streaming-aware pre-emit output guardrails.** Per above.
- **`IToolApprovalPolicy`** from the review's §9.6 list was skipped — overlaps with `IToolGuardrail.BeforeInvokeAsync` (same Pass / Deny / Interrupt shape). Don't ship both; they'd duplicate the surface.

## See also

- [Architecture](architecture.md)
- [Execution loop](execution-loop.md) — where each layer fires.
- [Add input and output guardrails guide](../guides/add-input-output-guardrails.md)
