# AgentGraphInProcess

Build and run a branching multi-agent graph entirely in C#. No YAML, no external services.

## Run

```bash
dotnet run --project samples/AgentGraphInProcess
```

## Expected output

```
== streaming run ==
  ► GraphStarted   entry=classify
    NodeStarted    [Agent] classify
    NodeAgentInvoked
    NodeCompleted  classify
    StateUpdated   keys=[lastAssistantText, messages, category]
    EdgeTraversed  classify → support-reply
    NodeStarted    [Agent] support-reply
    NodeAgentInvoked
    NodeCompleted  support-reply
    StateUpdated   keys=[lastAssistantText, messages]
    EdgeTraversed  support-reply → end
  ✓ GraphCompleted

== unary invoke ==
  query    = Buy the enterprise plan.
  category = sales
```

## What it demonstrates

- `AgentGraphManifest` — code-first graph definition with `GraphNode` (Agent / End kinds) and `GraphEdge` with `GraphEdgePredicate.PropertyMatcher`.
- `GraphStateBindings` — `Output: ["category"]` extracts a named field from the classifier's JSON response into graph state.
- `InProcessGraphOrchestrator<TState>` — runs the graph in-process; the typed `PipelineState` record round-trips through `ToBag`/`FromBag` via `System.Text.Json`.
- `StreamAsync` — yields `GraphStarted`, `NodeStarted`, `NodeAgentInvoked` (per Agent-kind node, before `NodeCompleted`), `NodeCompleted`, `EdgeTraversed`, `StateUpdated`, `GraphCompleted` events.
- `InvokeAsync` — unary variant that returns the final typed state.
- Scripted classifier returns `{"category":"support"}` JSON; output binding extracts `category`; `PropertyMatcher` routes to the correct branch.

## Graph shape

```
classify → [category == "support"] → support-reply → end
         → [always]                → sales-reply   → end
```

## Production extension

- Replace `ScriptedProvider` with an `OpenAiCompatProvider` pointed at any OpenAI-compatible endpoint.
- Add `IGraphCheckpointer` (e.g. `OrleansCheckpointer`) to persist per-super-step state — see `AgentGraphResumeOnOrleans`.
- Switch to `MafGraphOrchestrator` (see `AgentGraphMaf`) to enable fan-out / fan-in (`concurrent: true` edges).

## Docs

- [Graph orchestration](../../docs/concepts/graph-orchestration.md)
- [`AgentGraphYamlLoader`](../AgentGraphYamlLoader) — same graph defined in YAML
- [`AgentGraphMaf`](../AgentGraphMaf) — same graph on MAF Workflows
- [`AgentGraphResumeOnOrleans`](../AgentGraphResumeOnOrleans) — durable interrupt + resume
