# GraphHitlLiveMode

Live-mode Human-in-the-Loop (HITL) using `MafGraphOrchestrator`. A three-node content pipeline (draft → review → publish) pauses at an `Interrupt`-kind node and calls an inline async handler. If the handler returns a non-null state, the workflow continues to publish without restarting. If it returns null, the run is aborted with `GraphHitlAbortedException`. No process boundary, no checkpoint round-trip required.

## Run

```bash
dotnet run --project samples/GraphHitlLiveMode
```

## Expected output

```
== run 1 — approved ==
  ► GraphStarted    entry=draft
    NodeStarted     [Agent] draft
    StateUpdated    keys=[draft]
    NodeCompleted   draft
    EdgeTraversed   draft → review
    NodeStarted     [Interrupt] review
    GraphInterrupted nodeId=review  reason="Pending editorial review"
  [handler] reason="Pending editorial review" → approving
    StateUpdated    keys=[hitl.response]
    NodeCompleted   review
    EdgeTraversed   review → publish
    NodeStarted     [Agent] publish
    StateUpdated    keys=[published]
    NodeCompleted   publish
    EdgeTraversed   publish → end
  ✓ GraphCompleted

== run 2 — aborted ==
  ► GraphStarted    entry=draft
    NodeStarted     [Agent] draft
    StateUpdated    keys=[draft]
    NodeCompleted   draft
    EdgeTraversed   draft → review
    NodeStarted     [Interrupt] review
    GraphInterrupted nodeId=review  reason="Pending editorial review"
  [handler] reason="Pending editorial review" → aborting (null)
  ✗ GraphFailed     GraphHitlAbortedException
  caught: GraphHitlAbortedException nodeId=review

Done.
```

## What it demonstrates

- `IHitlAgentGraph<TState>.StreamWithHitlAsync(initial, context, handleInterrupt, ct)` — streams graph events and calls `handleInterrupt` inline at every `Interrupt`-kind node; contrast with `IResumableAgentGraph<TState>` which pauses and requires a later `ResumeStreamAsync` call.
- Handler delegate `Func<GraphInterrupted, CancellationToken, ValueTask<TState?>>` — receives the `GraphInterrupted` event (with `NodeId`, `InterruptId`, `Reason`) and returns updated state to continue, or `null` to abort.
- Handler return non-null → state merged under `"hitl.response"` key via `StateUpdated`, graph continues from the interrupt node's outgoing edge without re-executing the node body.
- Handler return null → `GraphFailed` yielded, then `GraphHitlAbortedException(nodeId)` thrown; caller wraps the `await foreach` in `try/catch` to handle it.
- `GraphNode("review", "Interrupt", InterruptReason: "...")` — `Interrupt`-kind node requires no `Ref` or `HandlerRef`; `InterruptReason` is surfaced on the event's `Reason` property.
- `MafGraphOrchestrator` — implements `IHitlAgentGraph<TState>` alongside `IAgentGraph<TState>` and `IResumableAgentGraph<TState>`; live-mode HITL is the same interface contract as `InProcessGraphOrchestrator`.

## Live mode vs. halt mode

| | Live mode (`IHitlAgentGraph`) | Halt mode (`IResumableAgentGraph`) |
|---|---|---|
| Handler timing | Inline async callback during `StreamWithHitlAsync` | Separate process call to `ResumeStreamAsync` |
| Checkpoint required | No | Yes — `IGraphCheckpointer` persists state |
| Useful when | Handler is automated or fast (e.g., policy check, UI modal) | Human turnaround time is long; process must survive |

## Docs

- [Graph orchestration](../../docs/concepts/graph-orchestration.md)
- [`AgentGraphResumeOnOrleans`](../AgentGraphResumeOnOrleans) — halt-mode HITL with durable Orleans checkpoint
- [`ToolGuardrailsAndInterrupt`](../ToolGuardrailsAndInterrupt) — halt-mode interrupt at the tool-guardrail layer
