# v0.4.0-preview — Guardrails pillar (§9.4 of the architectural review)

Tactical plan for the fourth pillar. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.4. Created 2026-04-18.

---

## Scope

Ship MAF's three-layer guardrail split as typed interfaces. Wire input + output guardrails into `StatefulAiAgent`. Tool-guardrail interface ships but its wiring is deferred to the execution-loop pillar (§9.5) when `IToolCallDispatcher` lands and gives us the per-tool-call seam.

**Design decisions settled 2026-04-18**:

1. **Denial throws** an `AgentGuardrailDeniedException` — `AskAsync`'s `Task<string>` shape is unchanged; denial is a caught-exception signal like provider failures. `TurnFailed` carries it out on the event bus.
2. **`GuardrailOutcome` carries decision + optional reason only** — dropped the `Replacement` field from the review's sketch. Text-replacement semantics differ per layer and clutter the v0.4 shape; easy to add later without a break.
3. **Enum values: `Pass` and `Deny` only.** `Interrupt` deferred — requires resume machinery that lives with the execution-loop pillar's `AgentInterrupt` type.
4. **`GuardrailTriggered` event deferred.** The review flagged it; I'm parking it until execution-loop also needs the `AgentEvent` surrogate regenerated (`ToolCallStarted`/`Completed`/`InterruptRaised`) so the Orleans codec-converters get updated once instead of twice. Guardrail denials are still fully observable via `TurnFailed` in v0.4.
5. **Streaming output guardrails**: applied after the accumulator drains (post-facto). Consumers who need strict pre-flight gating use `AskAsync`. Documented.

---

## Delivery — single PR

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`.

Tasks:

- [x] Abstractions: `GuardrailDecision { Pass, Deny }` enum.
- [x] Abstractions: `GuardrailLayer { Input, Output, Tool }` enum.
- [x] Abstractions: `GuardrailOutcome` record — `Decision`, optional `Reason`; static `Pass` singleton + `Deny(reason?)` factory.
- [x] Abstractions: `IInputGuardrail.EvaluateAsync(CompletionRequest, AgentContext, CT)`.
- [x] Abstractions: `IOutputGuardrail.EvaluateAsync(CompletionResponse, AgentContext, CT)`.
- [x] Abstractions: `IToolGuardrail.BeforeInvokeAsync(...)` + `AfterInvokeAsync(...)` — ships for v0.4, not wired until §9.5 (documented in XML).
- [x] Abstractions: `AgentGuardrailDeniedException(GuardrailLayer, string? reason)` — carries layer + reason; message contains layer + reason when supplied.
- [x] Core: `StatefulAgentOptions.InputGuardrails` + `OutputGuardrails` + `ToolGuardrails`.
- [x] Core: `StatefulAiAgent` — input guardrails run inside the AskAsync try-block after the context-packer stage (denial captured as `failure` for usage-sink + TurnFailed emission); output guardrails run inside the try-block between provider call and session append. In `StreamAsync`: input guardrails run before the stream loop (captured as `failure`, skipping the loop); output guardrails run after the accumulator drains, post-facto (deltas already emitted).
- [x] Tests — 12 new: 2 `GuardrailOutcomeTests`, 2 `AgentGuardrailDeniedExceptionTests`, 8 `StatefulAiAgentGuardrailIntegrationTests` covering input-pass, input-deny-no-provider-call-no-assistant-append, output-pass, output-deny, short-circuit-on-first-deny, deny-emits-TurnFailed-on-bus, streaming-input-deny-no-deltas, streaming-output-deny-post-facto-no-assistant-append.
- [x] `PublicAPI.Unshipped.txt` updates (12 Abstractions entries + 6 Core options entries).

Breaking-change ledger:

- None. Pure additions. Defaults preserve byte-for-byte behaviour.

---

## Progress log

- 2026-04-18 — plan created, five design decisions logged (throw on deny, no Replacement, Pass/Deny only, event deferred, streaming-post-facto).
- 2026-04-18 — PR complete on local working tree. Abstractions: `GuardrailDecision`, `GuardrailLayer`, `GuardrailOutcome`, `IInputGuardrail`, `IOutputGuardrail`, `IToolGuardrail`, `AgentGuardrailDeniedException`. Core: three `StatefulAgentOptions` slots + `StatefulAiAgent` wiring in both AskAsync and StreamAsync. 12 new tests, 185/185 non-container green, 0 warnings.
