# graph-code-authored

Compose and run a multi-agent graph entirely in C# — no YAML manifest, no runtime container required. Uses `InProcessGraphOrchestrator`, `InMemoryAgentRegistry`, `InMemoryCheckpointer`, and a deterministic echo provider so the sample is hermetic (no API key, no Docker).

**Concepts:** [graph orchestration](../../docs/concepts/graph-orchestration.md), [graph as a first-class deployable](../../docs/concepts/graph-as-deployable.md).
**Needs API key:** no.
**Code:** ~70 lines.

---

## What this shows

- Building an `AgentGraphManifest` from C# record constructors — nodes, edges, state bindings.
- `InProcessGraphOrchestrator<TState>` with a typed POCO state (`PipelineState` record).
- Unary `InvokeAsync` — drives the graph to completion and returns the final state.
- Streaming `StreamAsync` — emits every `AgentGraphEvent` in BSP super-step order: `graph.started`, `node.started`, `node.completed`, `edge.traversed`, `graph.completed`.
- Wiring a `InMemoryCheckpointer` for checkpoint-per-super-step semantics without external storage.

---

## Run

```bash
cd oss/agentic
dotnet run --project samples/graph-code-authored
```

Expected output:

```
Final state:
  input    = Hello from GraphCodeAuthored!
  a_output = [echo] Hello from GraphCodeAuthored!

Streaming events:
  [          GraphStartedEvent] graphId=two-step-pipeline runId=<id>…
  [     GraphNodeStartedEvent] graphId=two-step-pipeline runId=<id>…
  [   GraphNodeCompletedEvent] graphId=two-step-pipeline runId=<id>…
  [  GraphEdgeTraversedEvent] graphId=two-step-pipeline runId=<id>…
  [     GraphNodeStartedEvent] graphId=two-step-pipeline runId=<id>…
  [   GraphNodeCompletedEvent] graphId=two-step-pipeline runId=<id>…
  [  GraphEdgeTraversedEvent] graphId=two-step-pipeline runId=<id>…
  [        GraphCompletedEvent] graphId=two-step-pipeline runId=<id>…

Done.
```

---

## Key types

| Type | Package | Purpose |
|---|---|---|
| `AgentGraphManifest` | Abstractions | Declarative graph spec (nodes, edges, entry) |
| `GraphNode` | Abstractions | Node with kind (`Agent`, `Code`, `End`) and optional `Ref` |
| `GraphEdge` | Abstractions | Directed connection; optional `When` predicate |
| `GraphStateBindings` | Abstractions | Maps graph-state keys into/out of node invocations |
| `InProcessGraphOrchestrator<T>` | Core | BSP driver — typed or bag state |
| `InMemoryAgentLifecycleManager` | Core | Resolves agent handles without Orleans / HTTP |
| `InMemoryCheckpointer` | Core | In-process checkpoint store; no external storage |
| `InMemoryAgentGraphRegistry` | Core | Simple manifest store for tests + dev |

---

## See also

- [docs/concepts/graph-orchestration.md](../../docs/concepts/graph-orchestration.md) — full BSP model, edge predicates, effects, interrupt + resume
- [docs/guides/run-resumable-graphs-on-orleans.md](../../docs/guides/run-resumable-graphs-on-orleans.md) — durable checkpoints via Orleans
- [samples/graph-yaml-authored](../graph-yaml-authored) — same pipeline as YAML + `vais apply`
- [samples/graph-cross-runtime](../graph-cross-runtime) — adds a remote node ref
