# A2AInterruptResumeOrleans

Demonstrate durable A2A interrupt/resume backed by an Orleans silo. An agent raises an interrupt when a high-value tool call requires human approval; `OrleansTaskStore` persists the `input-required` task to an Orleans grain so the task survives silo restarts.

## Run

```bash
dotnet run --project samples/A2AInterruptResumeOrleans
```

## Expected output

```
Server: http://127.0.0.1:PORT/agents/approver

step 1 — state: InputRequired, taskId: <id>

step 2 — state: Completed
```

*(Port number and taskId vary.)*

## What it demonstrates

- `OrleansTaskStore` registered before `AddA2AAgentServer()` — `TryAddSingleton` in `AddA2AAgentServer` preserves the prior `ITaskStore` registration, so Orleans-backed storage wins over the default `InMemoryTaskStore`.
- `IToolGuardrail.BeforeInvokeAsync` → `GuardrailOutcome.Interrupt(interrupt)` — raises `AgentInterruptedException` from inside the agent loop.
- `A2AAgentHandler` interrupt path — catches `AgentInterruptedException`, runs the full Submit → StartWork → RequireInput lifecycle, and embeds the interrupt envelope as a data-part on the task status message.
- `SendMessageResponseCase.Task` — the first `SendMessageAsync` returns a `Task` (not a `Message`) when the agent is interrupted; the task state is `InputRequired`.
- Resume via `message.TaskId` — setting `TaskId` on the second message routes the call through `HandleResumeAsync`, which replays the prior task and transitions it to `Completed`.
- `IA2ATaskGrain` — Orleans grain keyed by `taskId` that backs `OrleansTaskStore`. In a production silo with Redis or Postgres storage, `input-required` tasks survive pod restarts between step 1 and step 2.

## Flow

```
client                A2AAgentHandler          InterruptOnceProvider  ApprovalGuardrail
  │─── msg (fresh) ──▶│                              │                      │
  │                   │── InvokeAsync ──────────────▶│                      │
  │                   │                   tool-call ◀│                      │
  │                   │                              │── BeforeInvokeAsync ─▶│
  │                   │                              │◀── Interrupt ─────────│
  │                   │◀── AgentInterruptedException ─│                      │
  │                   │── SubmitAsync + RequireInputAsync                    │
  │◀── Task(InputRequired) ─│                        │                      │
  │                         │                        │                      │
  │─── msg (taskId=…) ──────▶│                       │                      │
  │                   │── InvokeAsync (resume.*) ────▶│                      │
  │                   │                final answer ◀│                      │
  │                   │── CompleteAsync              │                      │
  │◀── Task(Completed) ──────│                        │                      │
```

## Production extension

- Replace `AddMemoryGrainStorage` with Redis or Postgres persistence (`OrleansRedisPersistence` / `OrleansPostgresPersistence` samples) to make `OrleansTaskStore` truly durable across restarts.
- Wire a real `ICompletionProvider` (e.g. `SemanticKernelCompletionProvider`) that calls an LLM for the actual agent logic.
- Add `AddA2AAgentServerJwtAuth(...)` + `UseAuthentication()` to protect the endpoint.

## Docs

- [Host agents as A2A endpoints](../../docs/guides/host-agents-as-an-a2a-endpoint.md)
- [Guardrails](../../docs/concepts/guardrails.md)
- [`A2AServerBasics`](../A2AServerBasics) — simpler A2A sample without interrupt/resume
