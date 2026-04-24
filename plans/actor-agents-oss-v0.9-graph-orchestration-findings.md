# v0.9 Graph orchestration — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.9-graph-orchestration-spike.md`](./actor-agents-oss-v0.9-graph-orchestration-spike.md). Answers Q1–Q4 with evidence, not opinion. Landing verdict at the bottom.

Created 2026-04-19. **Status**: complete. Q1 + Q2 delegated to parallel research agents (both returned); Q3 + Q4 synthesised locally. Code spike recommended as skippable — design collapses cleanly without a PoC.

---

## Q1 — MAF Workflows API surface + stability

Reference implementation: **Microsoft Agent Framework Workflows** (`Microsoft.Agents.AI.Workflows` — separate package from `Microsoft.Agents.AI`). Sources: [overview](https://learn.microsoft.com/en-us/agent-framework/workflows/), [executors](https://learn.microsoft.com/en-us/agent-framework/workflows/executors), [edges](https://learn.microsoft.com/en-us/agent-framework/workflows/edges), [HITL](https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop), [checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints), [declarative](https://learn.microsoft.com/en-us/agent-framework/workflows/declarative).

### Stability signal — GA, separate package

- `Microsoft.Agents.AI.Workflows` is **separate from `Microsoft.Agents.AI`** (same version line).
- Version history: 33 previews → `1.0.0-rc1..rc5` (Feb 2026) → `1.0.0` GA (early March 2026) → **`1.1.0` GA (2026-04-10)**. Latest GA = 1.1.0. No higher preview exists.
- ~5 months of weekly previews → 2 months on GA → one minor bump. Fast-moving but stable-major.
- **No "preview" banner on the Workflows package or its Learn docs.** Treated as first-class.
- **Adjacent packages still preview** (would inherit churn if we depended on them): `Microsoft.Agents.AI.Workflows.Declarative` (`1.1.0-rc1`), `Microsoft.Agents.AI.Hosting` (`1.1.0-preview.260410.1`).

### Core public types

- `WorkflowBuilder` / `Workflow`
- `Executor` (base) / `Executor<TIn, TOut>` — **fully generic** (any CLR type as I/O; workflow validates type compatibility between connected executors at build time)
- `IWorkflowContext` — node-side API for reading state + queueing state updates
- `[MessageHandler]` attribute + source generator
- `IResettableExecutor`
- `RequestPort` / `RequestPort.Create<TReq, TRes>(id)` — the HITL primitive
- `InProcessExecution` (static runner)
- `Run` / `StreamingRun` — run handles
- `WorkflowEvent` family: `ExecutorCompletedEvent`, `AgentResponseUpdateEvent`, `WorkflowOutputEvent`, `RequestInfoEvent`, `SuperStepCompletedEvent`
- `TurnToken` — barrier signal for agent-wrapped executors
- `CheckpointManager` / `CheckpointInfo`

Edge APIs on builder: `AddEdge`, `AddEdge(..., condition:)`, `AddSwitch`, `AddFanOutEdge`, `AddFanInBarrierEdge`, `WithOutputFrom`.

### Idiomatic shape

```csharp
var workflow = new WorkflowBuilder(frenchAgent)
    .AddEdge(frenchAgent, spanishAgent)
    .AddEdge(spanishAgent, englishAgent)
    .Build();

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
    workflow, new ChatMessage(ChatRole.User, "Hello"));
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
```

Execution model: **Pregel / BSP supersteps** with sync barriers between supersteps. Same model as LangGraph's (the two landed on it independently).

**Important**: there's **no `MapA2A`-style extension** for agents-into-workflows. An `AIAgent` is passed directly to `WorkflowBuilder` + `AddEdge(agentA, agentB)`; the framework internally wraps it in an agent-executor that buffers `ChatMessage`s until a `TurnToken` arrives.

### Cycles + HITL + checkpoints

- **Cycles**: yes, just back-edges. No API restriction; type-compatibility validation is the only rule. Dedicated samples exist (Writer-Critic feedback loop, Looping category).
- **HITL / interrupt-resume**: `RequestPort` primitive. Executor sends on the port → framework emits `RequestInfoEvent` → host supplies answer via `handle.SendResponseAsync(evt.Request.CreateResponse(value))`. Functionally equivalent to LangGraph's `interrupt()` / `Command(resume=...)`.
- **Checkpointing**: `CheckpointManager.CreateInMemory()` is the **only built-in** for C#. Resume via `InProcessExecution.ResumeStreamingAsync(workflow, savedCheckpoint, checkpointManager)`. Custom state via `OnCheckpointingAsync` / `OnCheckpointRestoredAsync` + `IWorkflowContext.QueueStateUpdateAsync` / `ReadStateAsync`. Writes are per-superstep; pending requests persist + re-emit on resume.

**Gap**: Python MAF has `FileCheckpointStorage` + `CosmosCheckpointStorage`; **C# does not** — durable checkpointing in .NET requires a custom `CheckpointManager` subclass. That's the natural home for `OrleansCheckpointManager` in our `Vais.Agents.Hosting.Orleans` package.

### Declarative YAML — exists but preview

- `Microsoft.Agents.AI.Workflows.Declarative` (+ `.Declarative.AzureAI`, `.Declarative.Mcp`), YAML-authored, loaded via `DeclarativeWorkflowOptions`. **Still `1.1.0-rc1` / pre-release.**
- Two YAML dialects (C# trigger-based vs. Python name-based) — not interchangeable. Actions include `If`, `ConditionGroup`, `Foreach`, `BreakLoop`, `ContinueLoop`, `GotoAction`, `Question`, `RequestExternalInput`, `RepeatUntil`.
- Implication for us: if we want a declarative story, **we can't safely depend on MAF's declarative layer** in v0.9. Our manifest shape (drafted in Q4 above) is the authoritative declarative surface for us; a future adapter could interop with MAF's YAML loader if the dialects stabilise.

### Integration friction — minimal

- Executors are fully generic. Our neutral `ChatTurn` / `CompletionRequest` records can be first-class I/O types (`Executor<ChatTurn, ChatTurn>`). No shape compromises.
- Two integration paths:
   1. Let custom executors own `ChatTurn` directly; bridge to MAF's `ChatMessage` only at agent-wrapper boundaries via a small shim (`ChatTurn → ChatMessage` / `ChatResponse → ChatTurn`).
   2. Wrap `AIAgent` ourselves in an `Executor<CompletionRequest, CompletionResponse>` so the workflow never sees MAF's chat types at all.
- Only real protocol detail to remember: emit `TurnToken` at the start of agent-bearing runs.

### Implication for v0.9 scoping

- **Wrappable, not "reinvent".** Shape maps cleanly onto our contracts.
- Adapter package name per earlier discussion: **`Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`** (parallel to `.Ai.MicrosoftAgentFramework` / `.Ai.SemanticKernel`).
- **SK parity gap**: SK has no `Workflows` equivalent. Two choices:
   - (a) Ship adapter only — SK consumers get "graphs via MAF adapter; SK agents still usable as nodes inside a MAF-hosted graph".
   - (b) Ship adapter **+ a minimal in-house `Vais.Agents.Orchestration.Graph.InProcess` implementation** (Pregel-style BSP over our neutral contracts, maybe ~500 LOC). Zero-dep, good for tests, lets SK-only stacks run graphs without adding MAF.
- Preliminary lean: (b). The in-house fallback is small and gives us a neutral reference implementation to validate the `IGraphOrchestrator` contract against — same pattern we used for `Vais.Agents.Hosting.InMemory` vs. `Vais.Agents.Hosting.Orleans`.

---

## Q2 — Cycles + checkpoint + resume story

Reference implementation: **LangGraph** (Python, 1.0.6 as of researched date). Sources: [persistence](https://docs.langchain.com/oss/python/langgraph/persistence), [interrupts](https://docs.langchain.com/oss/python/langgraph/interrupts), [graph-api](https://docs.langchain.com/oss/python/langgraph/graph-api), [durable-execution](https://docs.langchain.com/oss/python/langgraph/durable-execution).

### Checkpointer contract

Small 4-method interface (`BaseCheckpointSaver`):

- `.put(config, checkpoint, metadata, new_versions)` — store full checkpoint.
- `.put_writes(config, writes, task_id)` — record pending writes from partially-completed super-steps so successful nodes don't re-execute on resume (idempotency for multi-node super-steps).
- `.get_tuple(config)` — fetch one checkpoint by `thread_id` + `checkpoint_id`.
- `.list(config, ...)` — history for `get_state_history()`.

Plus `a*` async variants. Thread-scoped identity: `(thread_id, checkpoint_ns, checkpoint_id)` — `checkpoint_ns = ""` for root, `"node_name:uuid"` for subgraphs.

**Persists full accumulated channel state after reducers — not deltas**. One checkpoint per super-step boundary. A START→A→B→END graph produces ~4 checkpoints.

**Built-in checkpointers**: `InMemorySaver`, `SqliteSaver`/`AsyncSqliteSaver`, `PostgresSaver`/`AsyncPostgresSaver` (used by LangSmith in prod), `CosmosDBSaver`.

### `interrupt()` semantics

`interrupt(value)` inside a node **raises a special exception caught by the runtime**:
- Execution pauses at the super-step boundary; full state is checkpointed.
- Caller's `invoke/stream` surfaces the payload under `result["__interrupt__"]` (v1) or `chunk["interrupts"]` (v2).
- Resume: `graph.invoke(Command(resume=X), config=...)` **restarts the interrupting node from the top**; the `interrupt()` call then **returns X** at the same call site.
- Multiple interrupts in one node are matched to resume values by **index** — which is why LangGraph warns against non-deterministic interrupt loops or wrapping `interrupt()` in try/except.

### Cycles + guards

- Cycles = just edges back to an earlier node (typically via `add_conditional_edges(source, routing_fn)` where `routing_fn(state) -> next_node_name`).
- **Guard = global `recursion_limit`** (default **1000 super-steps** per invocation, `GraphRecursionError` on overrun).
- No per-node max-invocation counter — only the global ceiling.
- Nodes can degrade gracefully by reading `config["metadata"]["langgraph_step"]` or `RemainingSteps`.

### Human-in-the-loop minimal snippet

```python
from langgraph.checkpoint.memory import MemorySaver
from langgraph.graph import StateGraph, START, END
from langgraph.types import Command, interrupt

def approval_node(state):
    decision = interrupt("Approve action?")  # pauses here
    return {"approved": decision}

builder = StateGraph(dict)
builder.add_node("approval", approval_node)
builder.add_edge(START, "approval")
builder.add_edge("approval", END)

graph = builder.compile(checkpointer=MemorySaver())
config = {"configurable": {"thread_id": "t1"}}

graph.invoke({}, config=config)                    # returns __interrupt__
final = graph.invoke(Command(resume=True), config=config)
print(final["approved"])                           # True
```

### Checkpoint shape

Public surface = `StateSnapshot`: `{values, next, config, metadata: {source ∈ {input, loop, update}, writes, step}, created_at, parent_config, tasks}`. Structured on the outside; values serialised via `JsonPlusSerializer` (optionally `EncryptedSerializer`) — **serializer-opaque on the inside**. Cross-version portability is **unclear** from public docs; no migration contract published.

### Port-to-.NET feasibility on our Orleans journal

**Feasible and reasonably clean** — the checkpointer contract is small, thread-scoped, idempotency-safe via `put_writes`, and cadence (super-step boundary) fits Orleans grain checkpointing well.

Natural mapping:
- `thread_id` → our `RunId` (already stamped on `AgentContext.RunId`).
- `.put(...)` → append journal entry with `{values, next, metadata, parent_checkpoint_id}` payload to an `IAgentRunJournalGrain`-alike.
- Super-step boundary writes → one journal entry per tick, not per instruction.

**Harder bits we inherit, not solve**:
1. **Replay determinism discipline** — nodes must be deterministic + idempotent; side effects sit on the post-interrupt side or ride tool-call idempotency keys. Same user-facing contract LangGraph imposes.
2. **Index-based interrupt matching** — multiple `interrupt()` calls in one node presume deterministic ordering across replays. Portable, but re-executes the node from the top on resume.
3. Fork/branch/time-travel (`get_state_history() + parent_config` chains) is structurally supported by an append-only journal — no extra machinery needed.

**Reducers per channel** (`add_messages`, `operator.add`) are trivial to port.

### Implication for v0.9 scoping

- If we ship cycles + interrupt/resume day one, the checkpointer interface is ~4 methods — cheap to implement. **Journal extension shape, not new package**.
- `InMemoryCheckpointer` ships in `Vais.Agents.Core`; `OrleansCheckpointer` extends `Vais.Agents.Hosting.Orleans` (already has the storage story). Same split as v0.8's `InMemoryTaskStore` / `OrleansTaskStore`.
- The determinism discipline is a **documentation + guardrail** problem, not a runtime feature. We inherit LangGraph's reasoning unchanged. This is a user-facing contract we'd document in a new `docs/concepts/durable-graphs.md`.
- **Preliminary lean**: day-one cycles is defensible, not speculative. The machinery is small, the mapping is natural, and deferring cycles would leave the most interesting archetype (retrieval loops, self-reflection, ReAct) out of v0.9.

---

## Q3 — State model (typed generic vs. shared bag)

### Prior-art table

| Framework | State shape | Serialisation | Declarable? |
|---|---|---|---|
| **LangGraph** (Python) | Typed state per `StateGraph(StateClass)` + channel reducers (`add_messages`, `operator.add`) | Pydantic/JSON via `JsonPlusSerializer` | State schema can project to JSON Schema, but edge conditions + reducers are Python code |
| **MAF Workflows** (.NET) | Fully generic `Executor<TIn, TOut>` — any CLR type across edges; workflow validates type-compat at build | Consumer-owned; `IWorkflowContext.QueueStateUpdateAsync` / `ReadStateAsync` + `OnCheckpointingAsync` | Declarative YAML (preview) uses string/JSON-bag shape — diverges from the code-first typed model |
| **AutoGen GraphFlow** (Python) | Shared message bag across all nodes (no typed channels) | JSON | Yes, but no stateful channels — everything rides through messages |
| **OpenAI Swarm** | No persistent state — handoffs are stateless function-calls that swap the active agent | N/A | Trivially (no state to declare) |

Two clean design points emerge:
1. **Typed-generic-first (LangGraph / MAF)**: ergonomic + compile-safe in code, awkward to fully project to YAML because edge conditions and reducers want to be code-valued.
2. **Bag-first (AutoGen / Swarm)**: simple + declarative-friendly, but loses compile-safety and makes multi-node graphs awkward to debug.

### Our shape — hybrid with JSON-schema-declarable typed state

The v0.6 `AgentManifest.OutputSchema` precedent already proved consumers are happy declaring typed output contracts via JSON Schema inside YAML. Reuse that pattern for graph state. **Core abstraction shape**:

```csharp
// Neutral graph contract — in Vais.Agents.Abstractions.
public interface IAgentGraph<TState>
{
    ValueTask<TState> InvokeAsync(
        TState initial,
        AgentContext context,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentGraphEvent> StreamAsync(
        TState initial,
        AgentContext context,
        CancellationToken cancellationToken = default);
}

// Non-generic convenience for "bag" usage — TState = IDictionary<string, JsonElement>.
public interface IAgentGraph : IAgentGraph<IDictionary<string, JsonElement>> { }
```

This gives us both:
- **Code-first consumers** (C# devs authoring a graph in code) use `IAgentGraph<MyState>` with their own POCO — matches MAF's idiom directly.
- **Declarative consumers** (YAML-authored graphs) use `IAgentGraph` (non-generic) where state is a `IDictionary<string, JsonElement>` with a JSON Schema constraining the shape — matches Archetype B's `spec.state.schema` block.
- Both share the same orchestrator runtime. No forked code paths.

### Reducers

LangGraph's reducer pattern (`add_messages`, `operator.add`) is useful but not essential for v0.9. Ship with:
- **Default reducer = "last-write-wins"** for each state key. Covers 90% of cases.
- **`appendMessages` reducer** for the conversational-history key (same pattern as `Memory` subnode in VAIS2's subnodes architecture).
- Custom reducers = consumer code only in v0.9; YAML declarability for custom reducers deferred to a future pillar.

### JSON-schema-declarable state — the required property

For Archetype B's YAML to work, state fields must be JSON-schema-describable. That means:
- Primitive types: OK (`string`, `number`, `integer`, `boolean`).
- Arrays of primitives / simple objects: OK.
- Nested objects: OK.
- Arrays of `ChatTurn`: also OK (`ChatTurn` already has a stable public shape).
- Arbitrary closures, delegates, or non-serialisable .NET types: **disallowed** in declarative state. Code-first `TState` can hold anything.

### Recommended decision

**Ship hybrid.** `IAgentGraph<TState>` generic in the neutral contract; `IAgentGraph` (bag) as a specialisation over `IDictionary<string, JsonElement>`; YAML loader instantiates the bag-form via the existing JSON Schema validation infrastructure (same pattern `AgentManifest.OutputSchema` uses).

**Ships in v0.9. Cheap — two types, one default reducer, one well-known key for chat history.**

---

## Q4 — YAML declarative viability

### Archetype A — Pure handoff graph

The simplest possible graph: three agents, a router decides which downstream agent handles the user's query based on keyword-matching on the incoming message. No state; handoff is terminal (no loop back).

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: customer-router
  version: "1.0"
  description: Route customer queries to the right specialist agent.
spec:
  entry: triage

  nodes:
    - id: triage
      kind: Agent
      ref: { id: triage-agent, version: "1.0" }

    - id: billing
      kind: Agent
      ref: { id: billing-agent, version: "1.0" }

    - id: sales
      kind: Agent
      ref: { id: sales-agent, version: "1.0" }

  edges:
    - from: triage
      to: billing
      when:
        property: lastMessage.text
        operator: Contains
        value: refund
    - from: triage
      to: sales
      when:
        property: lastMessage.text
        operator: Contains
        value: upgrade
    - from: triage
      to: end
      when: always
```

**Verdict on Archetype A**: declarative shape holds trivially. `when` predicates map cleanly onto Kubernetes-style `{property, operator, value}` matchers (same idiom as `matchExpressions` on a PodSelector). No DSL needed. The `AgentGraph` manifest reuses the `metadata` block from `AgentManifest` unchanged; only `spec.nodes` + `spec.edges` are new.

One design question surfaced: the edges reference **agent ids that must already be registered** — either in the same registry or by a `ref.registry` field (for cross-tenant / cross-cluster lookups). For v0.9 we assume same-registry; the `ref` shape leaves room for `registry: "external-url"` later.

### Archetype B — LangGraph-style retrieval loop

A stateful graph with a cycle: retrieve docs, generate an answer, grade the answer's quality; if quality is below threshold, loop back to retrieval with refined query; else emit. This is the prototypical "ReAct with reflection" shape LangGraph is built for.

```yaml
apiVersion: vais.agents/v1
kind: AgentGraph
metadata:
  id: reflective-qa
  version: "1.0"
  description: RAG with self-grading + retry-until-good loop.
spec:
  entry: retrieve

  # Shared state carried through the graph. Typed as a JSON schema — keys
  # here are the only ones nodes can read/write without a handler-ref.
  state:
    schema:
      type: object
      properties:
        query:             { type: string }
        docs:              { type: array, items: { type: string } }
        answer:            { type: string }
        quality:           { type: number, minimum: 0, maximum: 1 }
        retryCount:        { type: integer, default: 0 }
      required: [query]

  nodes:
    - id: retrieve
      kind: Agent
      ref: { id: retrieval-agent, version: "1.0" }
      # Map graph state in/out on this node. Without the `state.bindings`
      # block, nodes only see the lastMessage + their own agent context.
      stateBindings:
        input:  [query, retryCount]
        output: [docs]

    - id: answer
      kind: Agent
      ref: { id: answering-agent, version: "1.0" }
      stateBindings:
        input:  [query, docs]
        output: [answer]

    - id: grade
      kind: Agent
      ref: { id: grading-agent, version: "1.0" }
      # A grading agent whose OutputSchema declares a numeric quality field;
      # the runtime extracts it into state.quality per the stateBindings.
      stateBindings:
        input:  [query, answer]
        output: [quality]

  edges:
    - from: retrieve
      to:   answer
      when: always
    - from: answer
      to:   grade
      when: always
    - from: grade
      to:   retrieve
      when:
        allOf:
          - { property: quality,    operator: Lt, value: 0.7 }
          - { property: retryCount, operator: Lt, value: 3   }
      # Optional: state mutation performed on this edge traversal.
      onTraverse:
        incrementState: [retryCount]
    - from: grade
      to:   end
      when: always   # fall-through (order-scanned; first match wins)

  # Guard against runaway loops — the runtime rejects a graph that could
  # exceed this count of node transitions in a single run.
  maxSteps: 20
```

**Verdict on Archetype B**: declarative shape mostly holds with three specific compromises — documented here so the pillar plan can land on a decision:

1. **The predicate language expands from `{property, operator, value}` to `{allOf, anyOf, not, ...}` combinators.** Kubernetes-style exactly. Still no code eval needed, but the spec does grow to a nested boolean DSL. Fine. Operators we need: `Eq`, `NotEq`, `Gt`, `Gte`, `Lt`, `Lte`, `Contains`, `NotContains`, `Exists`, `NotExists`. About 10 operators total — same as `matchExpressions`.

2. **State `stateBindings.output` assumes node produces structured output.** The *grading* node in this archetype needs to return a structured `{quality: 0.3}` rather than free text. That's already expressible via `AgentManifest.OutputSchema` (v0.6 ships that). Good — the integration is natural: a node with `stateBindings.output: [quality]` REQUIRES its agent to have a matching `OutputSchema` field, and the runtime wires extraction mechanically.

3. **`onTraverse.incrementState` is a tiny side-effect vocabulary on edges.** Alternatives: (a) ship this small vocabulary — `increment`, `set`, `append` on typed paths; (b) require a `handlerRef` for side effects (consumers write C#). Archetype B is the stress test — if we don't ship (a), this archetype REQUIRES a code-ref for `retryCount++`, which breaks the "80% declarative" goal. Recommendation: ship a tiny side-effect vocabulary (3–4 verbs) and leave `handlerRef` as the escape hatch for the 20% that needs it.

### What Archetype B would break

If we shipped **only** `{property, operator, value}` matchers with no combinators and no `onTraverse`, Archetype B couldn't be expressed declaratively — the loop guard (`quality < 0.7 AND retryCount < 3`) needs `allOf`, and the retry counter needs a mutation verb. Those are the concrete asks on the declarative layer.

If we shipped typed generic state (`IAgentGraph<TState>`) without a JSON-schema projection path, Archetype B couldn't be authored in YAML at all — the state shape would be a runtime C# type. Recommendation: pick **shared state with JSON-schema-declarable fields** as the core model, and layer a `TState` typed-wrapper *on top* for pure-C# consumers. This is the same pattern MAF takes (as far as we can see pre-Q1).

---

## Verdict — v0.9 pillar shape

**Spike conclusion: ready to write a v0.9 pillar plan.** No blockers remain; the design space collapses cleanly.

### Locked decisions

1. **Shape**: neutral `IAgentGraph<TState>` interface in Abstractions + `IAgentGraph` (bag) specialisation for declarative consumers. Hybrid model, single runtime.
2. **Adapter**: ship the MAF adapter — MAF Workflows is GA at 1.1.0, the shape maps cleanly, cycles + HITL + in-memory checkpointing all supported natively. **No need to reinvent LangGraph in .NET** for MAF consumers.
3. **In-house fallback**: ship a minimal `Vais.Agents.Orchestration.Graph.InProcess` too — small (~500 LOC estimate), Pregel-style BSP over our neutral contracts, so SK-only consumers and tests don't need to drag MAF in.
4. **Cycles + interrupt/resume ship day one.** LangGraph's checkpointer contract is 4 methods; MAF's is `CheckpointManager` + `OnCheckpointingAsync` hooks. Both are cheap to implement against our existing `OrleansAgentJournal` storage. Deferring cycles would leave the most interesting archetypes (retrieval loops, self-reflection, HITL approvals) out of v0.9.
5. **Checkpointer**: `InMemoryCheckpointer` in `Vais.Agents.Core`; `OrleansCheckpointer` extension on `Vais.Agents.Hosting.Orleans` (same split as v0.8's task store).
6. **Declarative YAML ships in v0.9.** Our own `kind: AgentGraph` manifest, extending the existing `JsonAgentManifestLoader` / `YamlAgentManifestLoader` infrastructure. **Does NOT depend on** `Microsoft.Agents.AI.Workflows.Declarative` — that package is still `rc1`/preview. Two-way interop with MAF's YAML dialect can land when it GAs.
7. **Edge predicates**: Kubernetes-style `{property, operator, value}` matchers with `allOf`/`anyOf`/`not` combinators (~10 operators). Code-ref (`handlerRef`) as the escape hatch for the 20% that needs it. No expression DSL.
8. **Edge side-effects**: tiny vocabulary on `onTraverse` — `increment` / `set` / `append` on typed paths. `handlerRef` for anything richer.

### Proposed v0.9 pillar scope

Three packages + one extension:
1. **`Vais.Agents.Abstractions`** (extend): `IAgentGraph<TState>`, `IAgentGraph`, `AgentGraphManifest`, `GraphNode`, `GraphEdge`, `GraphEdgePredicate`, `GraphEdgeEffect`, `AgentGraphEvent` taxonomy, `IGraphCheckpointer`.
2. **`Vais.Agents.Core`** (extend): `InProcessGraphOrchestrator` (Pregel/BSP runtime over neutral contracts), `InMemoryCheckpointer`, default reducers (`lastWriteWins`, `appendMessages`).
3. **`Vais.Agents.Control.Manifests.*`** (extend): `kind: AgentGraph` loader path, validation, envelope-JSON round-trip.
4. **`Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`** (new): thin adapter wrapping MAF `WorkflowBuilder` + `Executor<T>` behind `IAgentGraph<TState>`. **Zero new MAF concepts exposed**; consumers who want MAF's richer Workflows API drop down directly.
5. **`Vais.Agents.Hosting.Orleans`** (extend): `OrleansCheckpointer` + `IA2AGraphCheckpointGrain` (parallel shape to `IA2ATaskGrain`).

### Package count trajectory

21 (v0.8) → 22 (v0.9, if we ship `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` as the only new package; in-house runtime + abstractions live inside existing packages). No parallel SK adapter — SK agents still usable as nodes inside a MAF-hosted graph (MAF wraps them via `ChatClientAgent`), and SK-only stacks use the in-house orchestrator.

### PR split (preliminary — refined in pillar plan)

- **PR 1**: neutral `IAgentGraph<TState>` + `IAgentGraph` + `AgentGraphManifest` contracts + `InProcessGraphOrchestrator` + `InMemoryCheckpointer` + predicate evaluator + default reducers. Tests: both archetypes A + B runnable; no YAML yet.
- **PR 2**: YAML / JSON manifest loader for `kind: AgentGraph`. Archetypes A + B parse + round-trip. Validation + envelope-JSON.
- **PR 3**: MAF adapter package. Wraps our `IAgentGraph<TState>` over `WorkflowBuilder`. Tests: parity — same graph built two ways (in-process orch + MAF adapter) produces identical traces on deterministic inputs.
- **PR 4**: `OrleansCheckpointer` + durable resume. Tests: graph survives silo restart, HITL interrupts hold state across process restart.
- **PR 5**: v0.9.0-preview cut — API freeze + pack + smoketest extension + tag.

### What's explicitly deferred post-v0.9

- **Two-way YAML interop with MAF's `.Declarative` dialect** (wait for MAF's declarative to GA).
- **SK-native graph adapter** (SK has no Workflows; revisit if SK ships one).
- **Custom user-defined reducers declarable in YAML** (ship via `handlerRef` escape hatch first; declarative comes if patterns prove out).
- **Graph visualisation / editor integration** (same story as VAIS2's flow editor — separate concern).
- **Streaming LLM-driven "next node" selection** (AutoGen `SelectorGroupChat` shape — can be modeled as a special edge `kind: LlmRouter` in a future pillar).

### Effort estimate

Based on v0.7 + v0.8 pillar sizes (both 4-5 PRs, ~2 weeks elapsed):
- **5 PRs × ~2 days = ~2 weeks** elapsed for the core pillar.
- Highest-risk PR is **PR 4 (durable resume)** — interrupt/resume semantics are subtle; testing needs care. Likely ≥2 days.
- Lowest-risk PR is **PR 3 (MAF adapter)** — surface mostly already figured out via this spike.

### Code spike status

Not yet executed — based on the research, we have high confidence the shape works without needing a code PoC first. If the pillar plan uncovers a shape concern, a short code spike (`spike/agentic-graph-phase0/`) can validate it. **Recommendation: proceed directly to pillar plan; skip the code spike unless a surprise emerges during plan drafting.**

---

## Progress log

- 2026-04-19 — findings scaffold created. Q4 drafted locally (two YAML archetypes with declarative-viability verdict). Q1 (MAF Workflows) + Q2 (LangGraph checkpoint) delegated to parallel research agents. Q3 (state model) pending local synthesis after Q1/Q2 inputs land.
- 2026-04-19 — Q1 returned: MAF Workflows GA at 1.1.0 (2026-04-10), `Microsoft.Agents.AI.Workflows` is a separate package, fully-generic `Executor<T>` over any CLR type, cycles + `RequestPort` HITL + in-memory checkpoint manager native. Adjacent packages (`Declarative`, `Hosting`) still `rc1`/preview — don't depend on them. Q2 returned: LangGraph's checkpointer is 4 methods, thread-scoped (= our `RunId`), persists full state per super-step, `interrupt()` raises an exception caught by runtime, resume restarts the interrupting node from the top. Port-to-Orleans-journal is feasible and clean; the determinism discipline is a documentation problem, not a runtime feature.
- 2026-04-19 — Q3 landed: hybrid `IAgentGraph<TState>` + `IAgentGraph` (bag over `IDictionary<string, JsonElement>`) model. Code-first uses the generic, YAML-first uses the bag; both share runtime. Reducers default to last-write-wins + `appendMessages` well-known key; custom reducers are consumer code in v0.9.
- 2026-04-19 — Verdict landed. 8 decisions locked. Proposed v0.9 pillar: 5 PRs, 1 new package (`Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`) + extensions to `Abstractions` + `Core` + `Control.Manifests.*` + `Hosting.Orleans`. Package count 21 → 22. Effort estimate ~2 weeks. **Spike complete — ready to write pillar plan.**
