# AgentGraphYamlLoader

Load a graph manifest from a YAML file and run it in-process. Identical graph shape and output to `AgentGraphInProcess` — the only difference is the manifest is authored in `triage-graph.yaml` instead of C#.

## Run

```bash
dotnet run --project samples/AgentGraphYamlLoader
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
    ...
  ✓ GraphCompleted

== unary invoke ==
  query    = Buy the enterprise plan.
  category = sales
```

## What it demonstrates

- `YamlAgentGraphManifestLoader.LoadFromFileAsync` — parses a YAML `AgentGraph` manifest into an `AgentGraphManifest` record.
- `InProcessGraphOrchestrator` (non-generic, bag-state) — accepts `IDictionary<string, JsonElement>` directly; no typed-state record needed.
- `when: {property: category, operator: Eq, value: "support"}` — YAML syntax for `GraphEdgePredicate.PropertyMatcher`.
- `stateBindings.output: [category]` — YAML node output binding.

## YAML format

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: triage-graph
  version: "1.0"
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref: { id: classifier }
      stateBindings:
        output: [category]
    - id: support-reply
      kind: Agent
      ref: { id: support }
    - id: end
      kind: End
  edges:
    - from: classify
      to: support-reply
      when:
        property: category
        operator: Eq
        value: "support"
```

## Docs

- [Graph orchestration](../../docs/concepts/graph-orchestration.md)
- [`AgentGraphInProcess`](../AgentGraphInProcess) — same graph in C#
