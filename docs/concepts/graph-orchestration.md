# Graph orchestration

Shipped in v0.9 as the third orchestration style after Sequential / RoundRobin (v0.4) and Handoff (v0.4 data contract, consumer-authored control flow). Adds state-threaded, multi-step, checkpointable graphs with Pregel/BSP semantics. Same shape shared across LangGraph + MAF Workflows; Vais.Agents ships both a zero-MAF-dep in-process runner (`InProcessGraphOrchestrator`) and a MAF-Workflows adapter (`MafGraphOrchestrator`).

## What it is

A graph = a set of **nodes** connected by **edges**; nodes mutate a shared **state bag** as they execute; edges decide which node runs next based on predicate matches against state. The orchestrator drives a BSP (bulk synchronous parallel) super-step loop: at each super-step it picks the active node, runs it, applies state mutations, evaluates outgoing edges, picks the next active node, and iterates — until it reaches an `End` node, hits an `Interrupt` node, or trips the max-step ceiling.

Two state models ship:

- **Typed** — `IAgentGraph<TState>` — any POCO that round-trips through `System.Text.Json`. Code-first graphs in consumer apps.
- **Shared-bag** — `IAgentGraph : IAgentGraph<IDictionary<string, JsonElement>>` — state is a dictionary of `JsonElement` values, shape constrained by the manifest's `StateSchema` JSON Schema. YAML-authored graphs.

Both share the same runtime — `InProcessGraphOrchestrator<TState>` implements both by inheritance.

## Core types

`Vais.Agents.Abstractions`:

| Type | Purpose |
|---|---|
| `IAgentGraph<TState>` | Orchestrator interface. `InvokeAsync` runs to completion; `StreamAsync` yields the event taxonomy. |
| `IAgentGraph` | Specialisation over the shared-bag state shape. |
| `IResumableAgentGraph<TState>` | Capability interface — adds `ResumeAsync(runId, resumeInput, context)` for interrupt → resume. |
| `AgentGraphManifest` | Declarative shape. `Id`, `Version`, `Entry` node id, `Nodes[]`, `Edges[]`, optional `StateSchema`, `MaxSteps`. |
| `GraphNode` | `{Id, Kind, Ref?, HandlerRef?, StateBindings?, InterruptReason?}`. Four kinds: `Agent`, `Code`, `Interrupt`, `End`. |
| `GraphEdge` | `{From, To, When?: GraphEdgePredicate, OnTraverse?: GraphEdgeEffect, Concurrent: bool}`. `Concurrent=true` opts an edge into fan-out / fan-in under `MafGraphOrchestrator` (ignored by `InProcessGraphOrchestrator`). |
| `GraphEdgePredicate` | Closed hierarchy: `Always`, `PropertyMatcher`, `AllOf`, `AnyOf`, `Not`, `HandlerRef`, `Expression` (inline PowerFx boolean, evaluated by a registered `IGraphExpressionEvaluator` from `Vais.Agents.Core.PowerFx`). |
| `GraphPredicateOperator` | Ten operators (`Eq`, `NotEq`, `Gt`, `Gte`, `Lt`, `Lte`, `Contains`, `NotContains`, `Exists`, `NotExists`). See [reference](../reference/graph-predicate-operators.md). |
| `GraphEdgeEffect` | `{Kind, Property, Value?, HandlerRef?}`. Effects: `Set`, `Increment`, `Append`, `HandlerRef`. |
| `IGraphCodeNode` / `IGraphEdgePredicate` / `IGraphEdgeEffect` | DI hooks for `HandlerRef` escapes. |
| `IGraphCheckpointer` | Persist / load / delete `GraphCheckpoint { RunId, GraphId, SuperStep, NextNodeId?, State }` records keyed by `runId`. `InMemoryCheckpointer` in Core; `OrleansCheckpointer` in Hosting.Orleans. |
| `AgentGraphEvent` | Closed hierarchy, 10 subtypes — `GraphStarted`, `NodeStarted`, `NodeAgentInvoked`, `NodeCompleted`, `EdgeTraversed`, `StateUpdated`, `GraphInterrupted`, `GraphResumed`, `GraphCompleted`, `GraphFailed`. See [events reference](../reference/events.md). |

## Orchestrators

### `InProcessGraphOrchestrator<TState>` (in `Vais.Agents.Core`)

Zero-MAF-dep, synchronous-at-the-super-step-boundary BSP runner. Implements `IAgentGraph<TState>` + `IResumableAgentGraph<TState>`.

```csharp
using Vais.Agents.Core;

var orchestrator = new InProcessGraphOrchestrator<MyState>(
    manifest: graphManifest,
    registry: agentRegistry,
    lifecycle: lifecycleManager,
    checkpointer: new InMemoryCheckpointer());
var final = await orchestrator.InvokeAsync(initialState, context, ct);
```

Resolvers (`predicateResolver`, `effectResolver`, `codeNodeResolver`) let you plug DI-backed handlers into `HandlerRef` escapes. Null means handler-ref entries throw at traversal time.

### `MafGraphOrchestrator<TState>` (in `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`)

Translates the manifest into an MAF `Workflow` and executes via `InProcessExecution`. Useful when the host already runs MAF Workflows and wants a single executor surface. Implements `IAgentGraph<TState>` + `IResumableAgentGraph<TState>` + `IHitlAgentGraph<TState>` — same surface area as `InProcessGraphOrchestrator`.

```csharp
using Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

var orchestrator = new MafGraphOrchestrator<MyState>(
    manifest: graphManifest,
    registry: agentRegistry,
    lifecycle: lifecycleManager);
var final = await orchestrator.InvokeAsync(initialState, context, ct);
```

Adds native fan-out / fan-in via `GraphEdge.Concurrent = true` (all concurrent edges from the same source node fire in parallel; all concurrent edges pointing to the same target form a barrier join). Pair with `kind: Code` or a dedicated `GraphJoinNodeExecutor` for the join body. `InProcessGraphOrchestrator` ignores the flag and runs sequentially.

Call `MafGraphBuilder.Build(manifest, …)` directly when you want raw access to the underlying `Workflow` for richer MAF features (sub-workflows, native conditional edges, custom executors). The orchestrator is deliberately thin.

## Node kinds

| Kind | Runs | Required fields |
|---|---|---|
| `Agent` | `IAgentLifecycleManager.InvokeAsync(handle, request)` using `Ref.{Id, Version}`. `StateBindings.Input` becomes request metadata; the agent's structured output is extracted per `StateBindings.Output`. | `Ref` |
| `Code` | DI-resolved `IGraphCodeNode.ExecuteAsync(stateSubset, context, ct)`. `StateBindings.Input` filters the dictionary passed in; `StateBindings.Output` filters the dictionary merged back. | `HandlerRef` |
| `Interrupt` | Emits `GraphInterrupted` event; orchestrator writes a checkpoint and returns. Caller resumes via `IResumableAgentGraph<TState>.ResumeAsync(runId, input, context)`. | (neither `Ref` nor `HandlerRef` used) |
| `End` | Terminal. Orchestrator emits `GraphCompleted` and returns the final state. No outgoing edges evaluated. | — |

Closed set; additional kinds land additively per-pillar.

## Edges + predicates

Edges from the same source node are evaluated **in manifest order**; first matching predicate wins. At least one always-true edge per source node is the convention for reachability of `End`.

The predicate vocabulary is Kubernetes-style matchers — same idiom as `matchExpressions` on a `PodSelector`. Dotted paths address nested state; the well-known `lastMessage.text` and `lastMessage.role` paths read the most-recently-appended message from the `messages` state key (convention shared with LangGraph).

For conditions the matcher vocabulary can't express, use the `Expression` predicate — an inline PowerFx boolean (`when: "=<expr>"` in YAML) evaluated against state by a registered `IGraphExpressionEvaluator` (`Vais.Agents.Core.PowerFx` ships one via `AddPowerFxExpressionEvaluator()`; the runtime container wires it automatically). See the [route-graph-edges-with-powerfx tutorial](../agent-developer/route-graph-edges-with-powerfx.md).

YAML example (full 3-node graph, v0.6 envelope shape):

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: support-triage
  version: "1.0"
spec:
  entry: classify
  nodes:
    - id: classify
      kind: Agent
      ref: { id: classifier-agent, version: "1.0" }
      stateBindings:
        input:  [user_query]
        output: [category]
    - id: handle-billing
      kind: Agent
      ref: { id: billing-agent, version: "1.0" }
    - id: handle-technical
      kind: Agent
      ref: { id: technical-agent, version: "1.0" }
    - id: done
      kind: End
  edges:
    - from: classify
      to: handle-billing
      when: { property: category, operator: Eq, value: billing }
    - from: classify
      to: handle-technical
      when: { property: category, operator: Eq, value: technical }
    - from: handle-billing
      to: done
    - from: handle-technical
      to: done
```

Authored in C# via `InProcessGraphOrchestrator` ctor + `AgentGraphManifest` records; authored in YAML via the `Vais.Agents.Control.Manifests.Yaml` loader (same package that loads `kind: Agent` manifests).

See [compose-an-agent-graph-yaml guide](../guides/compose-an-agent-graph-yaml.md) for the end-to-end YAML walkthrough.

## Edge effects

Take-on-traverse mutations. Four kinds:

| Effect | Semantics |
|---|---|
| `Set` | Assign `Value` to `Property` in state. Overwrites. |
| `Increment` | Numeric-add `Value` to `Property`. Initialises to `0` if absent. |
| `Append` | Array-append `Value` to the list at `Property`. Initialises to `[]` if absent. |
| `HandlerRef` | Dispatch to a DI-resolved `IGraphEdgeEffect`. Escape hatch for arbitrary state transforms. |

Applied **after** the predicate matches, **before** the target node runs. Triggers a `StateUpdated` event carrying the changed keys.

## Checkpointing + resume

Every super-step boundary writes a `GraphCheckpoint { RunId, GraphId, SuperStep, NextNodeId?, State }` via `IGraphCheckpointer`. On an `Interrupt` node, the checkpoint persists the last-seen state + the node that would run next; the caller comes back days later with:

```csharp
IResumableAgentGraph<MyState> resumable = orchestrator;
var checkpoint = await checkpointer.LoadAsync(runId, ct);
var final = await resumable.ResumeAsync(
    checkpoint: checkpoint!,
    resumePayload: userApprovalPayload,     // merged under state["resume.payload"]
    context: context,
    cancellationToken: ct);
```

The interrupt node itself does not re-fire on resume — the orchestrator walks its outgoing edges against the rehydrated-plus-merged state.

Two checkpointers ship:

- **`InMemoryCheckpointer`** — `Vais.Agents.Core`. Dev + tests. `ConcurrentDictionary`-backed.
- **`OrleansCheckpointer`** — `Vais.Agents.Hosting.Orleans`. Production. Serialises checkpoints to `GraphRunCheckpointGrain` keyed by `runId`. Persistence flows through the configured Redis / Postgres provider.

Register with `AddOrleansGraphCheckpointer()`. Runs across silo restart, node migration, and operator redeploy. See [run-resumable-graphs-on-orleans guide](../guides/run-resumable-graphs-on-orleans.md).

## Run control across silos

In a clustered deployment, run conflict detection and cancellation are cluster-wide via `IAgentGraphRunGrain` (one grain per `runId`), wired by `AddOrleansGraphRunCoordinator()`:

- **Cancel from any silo.** `IAgentGraphLifecycleManager.CancelAsync(handle, runId)` routed to any silo signals the run grain; the silo executing the run observes the flag and stops cooperatively at the next super-step (≤ ~1 s plus the current node's duration — cancelling a node mid-LLM-call takes effect when the call returns).
- **Duplicate-run rejection is cluster-wide.** A second `InvokeAsync` with an in-flight `runId` fails with `GraphRunConflictException` regardless of which silo receives it.

Single-process and library hosts use the in-process default (`InProcessGraphRunCoordinator`); the runtime swaps in the grain-backed coordinator automatically.

**Scaling contract (P5).** Per-run cancel and conflict detection are cluster-wide. The graph-level aggregate counters from `QueryAsync` (active / completed / pending-interrupt) are **per-silo and advisory** — they reset on restart and do not sum across the cluster. There is no transparent mid-run failover: if the silo executing a run dies, recover by resuming from the last checkpoint (above), not by automatic grain reactivation.

## Events

`StreamAsync` yields the full `AgentGraphEvent` taxonomy — ten subtypes covering graph start, node start/invocation/end, edge traversal, state mutation, interrupt/resume, and terminal success/failure. Every event carries `{RunId, SuperStep}` so consumers correlate against the checkpoint timeline. `NodeAgentInvoked` appears after `NodeStarted` and before `NodeCompleted` for every `Agent`-kind node; it carries `InputText`, `OutputText`, and token counts. `StateUpdated` always follows `NodeCompleted`.

See the [events reference](../reference/events.md) for the full table + wire names.

## Extension points

- **`IGraphCodeNode`** — custom per-node logic outside the agent shape (e.g. deterministic transformation, external REST calls, custom scoring).
- **`IGraphEdgePredicate`** — predicates richer than the matcher vocabulary (e.g. compare two state properties; regex match; evaluate against external config).
- **`IGraphEdgeEffect`** — state mutations richer than `Set`/`Increment`/`Append` (e.g. deep-merge dictionaries; crypto-sign a value).
- **Custom `IGraphCheckpointer`** — swap Orleans for your own durable store.

All four are resolved from DI at invocation time by the orchestrator's constructor-injected resolver delegates.

## Relationship to the v0.4 orchestrators

`SequentialOrchestrator`, `RoundRobinOrchestrator`, and the `Handoff` data contract stay shipped + supported. Graph orchestration is a **sibling**, not a replacement — v0.4 orchestrators fit linear pipelines and speaker-list debates where shared state, interrupts, and per-super-step checkpoints would be overkill.

| Choose… | When… |
|---|---|
| `SequentialOrchestrator` | Straight-line pipeline, output of step N feeds step N+1 as the user message. |
| `RoundRobinOrchestrator` | N agents take turns around a shared conversation, termination predicate decides when to stop. |
| `Handoff` | One agent decides to pass control to another; the receiving agent replaces the sender. |
| `IAgentGraph` | State-threaded multi-step flow with conditional branching, cycles, or HITL interrupts. |

## Current limitations

- **Predicate values are scalar only.** `Gt` / `Gte` / `Lt` / `Lte` require numeric `Value`; `Contains` / `NotContains` operate on strings or arrays of scalars. Complex structural predicates go through `HandlerRef`.
- **No dedicated `Fork` / `Join` node kinds.** `Kind` is a fixed set of four; fan-out / fan-in is expressed on the edge (`GraphEdge.Concurrent = true`) under `MafGraphOrchestrator`. A dedicated `GraphJoinNodeExecutor` ships for join-body work, but there is no `Fork` / `Join` kind in the manifest grammar.
- **No sub-graphs** (`GraphInGraph` kind). Compose manually by invoking one graph from a `Code` node in another.
- **State merge is shallow.** Node outputs overwrite keys rather than deep-merging. Hand-write an `IGraphEdgeEffect` for merge semantics.

## See also

- [Orchestration concept](orchestration.md) — v0.4 Sequential / RoundRobin / Handoff primitives.
- [Graph predicate operators reference](../reference/graph-predicate-operators.md) — operator table + JSON shape examples.
- [Events reference](../reference/events.md) — `AgentGraphEvent` closed hierarchy.
- [Compose an agent graph (YAML) guide](../guides/compose-an-agent-graph-yaml.md)
- [Run resumable graphs on Orleans guide](../guides/run-resumable-graphs-on-orleans.md)
- [`samples/AgentGraphInProcess`](../../samples/AgentGraphInProcess) + [`samples/AgentGraphYamlLoader`](../../samples/AgentGraphYamlLoader) + [`samples/AgentGraphMaf`](../../samples/AgentGraphMaf) + [`samples/AgentGraphResumeOnOrleans`](../../samples/AgentGraphResumeOnOrleans) — runnable walkthroughs.
