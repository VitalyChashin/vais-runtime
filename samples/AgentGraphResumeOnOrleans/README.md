# AgentGraphResumeOnOrleans

Interrupt a graph mid-run, persist the checkpoint to an in-memory Orleans grain, then resume from the saved checkpoint. No external services needed — Orleans runs in-process with `AddMemoryGrainStorage`.

## Run

```bash
dotnet run --project samples/AgentGraphResumeOnOrleans
```

## Expected output

```
== phase 1: run until interrupt ==
  ► GraphStarted   entry=classify
    NodeStarted    [Agent] classify
    NodeAgentInvoked
    NodeCompleted  classify
    StateUpdated   keys=[lastAssistantText, messages, category]
    EdgeTraversed  classify → approve
  ⏸ GraphInterrupted  node=approve  reason=Pending human review  runId=<id>

== phase 2: load checkpoint from Orleans ==
  runId       = <id>
  nextNode    = approve
  category    = support
  isComplete  = False

== phase 3: resume from checkpoint ==
  ► GraphResumed   from=approve
    EdgeTraversed  approve → reply
    NodeStarted    [Agent] reply
    NodeAgentInvoked
    NodeCompleted  reply
    StateUpdated   keys=[lastAssistantText, messages]
    EdgeTraversed  reply → end
  ✓ GraphCompleted
```

## What it demonstrates

- `GraphNode("approve", "Interrupt", InterruptReason: "...")` — pauses the graph and emits `GraphInterrupted`.
- `OrleansCheckpointer(IGrainFactory)` — persists `GraphCheckpoint` to `IGraphCheckpointGrain` backed by `AddMemoryGrainStorage`.
- `GraphInterrupted.RunId` — the key needed to load the checkpoint after interruption.
- `IGraphCheckpointer.LoadAsync(runId)` — reloads the state snapshot written at interrupt time.
- `InProcessGraphOrchestrator.ResumeStreamAsync(checkpoint, resumePayload, ctx)` — resumes execution, emitting `GraphResumed` first, then continuing from the interrupt node's outgoing edges (node body is skipped).
- `Host.CreateDefaultBuilder().UseOrleans(...)` with `UseLocalhostClustering()` + `AddMemoryGrainStorage` — zero-dependency in-process silo for local development.

## Graph shape

```
classify → approve (Interrupt) → reply → end
```

## Production extension

- Swap `AddMemoryGrainStorage` for `AddRedisGrainStorage` (see `OrleansRedisPersistence`) to survive silo restarts.
- Store `runId` in your own data layer (e.g., a task table) and resume from a different process or silo instance.
- Combine with `OrleansTaskStore` (see `A2AInterruptResumeOrleans`) to surface the interrupt as an A2A `InputRequired` task.

## Docs

- [Graph orchestration](../../docs/concepts/graph-orchestration.md)
- [`AgentGraphInProcess`](../AgentGraphInProcess) — same graph without durable checkpointing
- [`A2AInterruptResumeOrleans`](../A2AInterruptResumeOrleans) — interrupt exposed over the A2A protocol
