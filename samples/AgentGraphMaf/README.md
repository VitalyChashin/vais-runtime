# AgentGraphMaf

Run the same triage graph shape using `MafGraphOrchestrator` (Microsoft Agent Framework Workflows). Proves cross-stack parity: identical manifest, identical output, different execution engine.

## Run

```bash
dotnet run --project samples/AgentGraphMaf
```

## Expected output

```
== streaming run ==
  ► GraphStarted   entry=classify
    NodeStarted    [classify] classify
    StateUpdated   keys=[lastAssistantText, messages, category]
    EdgeTraversed  classify → support-reply
    NodeCompleted  classify
    ...
  ✓ GraphCompleted

== unary invoke ==
  query    = Buy the enterprise plan.
  category = sales
```

*(Event ordering differs slightly from InProcess — MAF emits `StateUpdated` before `NodeCompleted`.)*

## What it demonstrates

- `MafGraphOrchestrator` — projects an `AgentGraphManifest` onto a MAF `Workflow`; same `IAgentGraph` contract as `InProcessGraphOrchestrator`.
- No `checkpointer` required for basic use — pass `IGraphCheckpointer` to enable durable resume.
- Fan-out / fan-in: MAF supports `concurrent: true` edges (`Concurrent = true` in C#) for parallel branch execution; `InProcessGraphOrchestrator` is sequential-only.
- `IDictionary<string, JsonElement>` bag-state — no typed record needed.

## When to choose MAF vs InProcess

| Capability | `InProcessGraphOrchestrator` | `MafGraphOrchestrator` |
|---|---|---|
| Sequential execution | ✓ | ✓ |
| Fan-out / fan-in | — | ✓ |
| HITL (`StreamWithHitlAsync`) | ✓ | ✓ |
| Durable resume | ✓ (with checkpointer) | ✓ (with checkpointer) |
| External MAF deps | — | `Microsoft.Agents.AI.Workflows` |

## Docs

- [Graph orchestration](../../docs/concepts/graph-orchestration.md)
- [`AgentGraphInProcess`](../AgentGraphInProcess) — sequential in-process orchestrator
