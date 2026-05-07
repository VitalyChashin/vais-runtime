# GraphPowerFxPredicates

Write conditional graph edge routing as inline PowerFx expressions — no `IGraphEdgePredicate` class required. A YAML graph manifest routes the planner's output to an analyst node or directly to end based on `=Not(IsBlank(Local.research_plan))` and `=IsBlank(Local.research_plan)`. Two scripted runs show both branches.

## Prerequisites

`Vais.Agents.Core.PowerFx` must be packed before it appears in the local feed:

```bash
dotnet pack src/Vais.Agents.Core.PowerFx -o artifacts/packages/
```

## Run

```bash
dotnet run --project samples/GraphPowerFxPredicates
```

## Expected output

```
== run 1 — non-blank plan → analyst ==
  ► GraphStarted   entry=planner
    NodeStarted    [Agent] planner
    NodeAgentInvoked
    NodeCompleted  planner
    StateUpdated   keys=[lastAssistantText, messages, research_plan]
    EdgeTraversed  planner → analyst
    NodeStarted    [Agent] analyst
    NodeAgentInvoked
    NodeCompleted  analyst
    StateUpdated   keys=[lastAssistantText, messages]
    EdgeTraversed  analyst → end
  ✓ GraphCompleted

== run 2 — blank plan → end (analyst skipped) ==
  ► GraphStarted   entry=planner
    NodeStarted    [Agent] planner
    NodeAgentInvoked
    NodeCompleted  planner
    StateUpdated   keys=[lastAssistantText, messages, research_plan]
    EdgeTraversed  planner → end
  ✓ GraphCompleted

Done.
```

## What it demonstrates

- `when: "=Not(IsBlank(Local.research_plan))"` — PowerFx expression as a YAML edge predicate; any string starting with `=` is parsed as `GraphEdgePredicate.Expression` by `YamlAgentGraphManifestLoader` / `JsonAgentGraphManifestLoader`.
- `PowerFxGraphExpressionEvaluator` — `IGraphExpressionEvaluator` implementation that evaluates PowerFx boolean expressions. Constructed directly (`new PowerFxGraphExpressionEvaluator()`) or via `AddPowerFxExpressionEvaluator()` in DI.
- `expressionEvaluator` constructor parameter on `InProcessGraphOrchestrator` — pass the evaluator to enable `Expression` predicates; omitting it throws `InvalidOperationException` if the manifest has any `=...` edges.
- State key access via `Local.*` — all graph state keys are available as `Local.<key>` in PowerFx. Hyphens in key names are normalized to underscores (`research-plan` → `Local.research_plan`).
- `IsBlank(Local.research_plan)` evaluates to `true` for absent or empty-string values; `Not(IsBlank(...))` for the non-empty branch.
- `YamlAgentGraphManifestLoader.LoadFromFileAsync` — load the manifest from a YAML file at runtime; `research-graph.yaml` is copied to the output directory via `CopyToOutputDirectory="PreserveNewest"`.

## Docs

- [Graph orchestration](../../docs/concepts/graph-orchestration.md)
- [`AgentGraphYamlLoader`](../AgentGraphYamlLoader) — YAML-loaded graph without PowerFx; uses `PropertyMatcher` predicates
- [`AgentGraphInProcess`](../AgentGraphInProcess) — same graph authoring in C# with `PropertyMatcher`
