# v0.42 — HITL / RequestPort-backed MAF graph interrupts

Closes the v0.9 deferral: "RequestPort-backed HITL — v0.9 interrupts use the simpler yield+halt-then-re-invoke pattern; RequestPort wiring tracks alongside durable resume in PR 4 (or v0.10 when MAF's CheckpointManager integration lands)." Created 2026-04-25. Grounded in an API survey of `Microsoft.Agents.AI.Workflows 1.1.0` (`E:/nugets/microsoft.agents.ai.workflows/1.1.0/lib/net9.0/Microsoft.Agents.AI.Workflows.xml`).

---

## Scope

**MVP boundary locked 2026-04-25.** Seven decisions:

1. **Live-session HITL, not re-invoke.** The v0.9 pattern checkpoints-and-halts then re-builds the workflow on resume (`ResumeAsync` / `ResumeStreamAsync` via `IResumableAgentGraph<TState>`). This pillar adds a second, orthogonal mode: the MAF workflow stays *open* during the interrupt, the handler is called from within the streaming enumeration, and the response is fed back via `StreamingRun.SendResponseAsync`. No process boundary is required; no durable checkpoint need be written. The two modes remain independent — `IResumableAgentGraph<TState>` is unchanged.
2. **New `IHitlAgentGraph<TState>` capability interface.** Parallel to `IResumableAgentGraph<TState>` (v0.34). Two methods: `StreamWithHitlAsync` + `InvokeWithHitlAsync`, both accepting a `Func<GraphInterrupted, CancellationToken, ValueTask<TState?>>` callback. Capability-interface shape means a future orchestrator that cannot support HITL doesn't need a stub.
3. **Both orchestrators implement the interface.** `InProcessGraphOrchestrator<TState>` gets a symmetric implementation: on `GraphInterrupted`, call the handler inline, then feed the result into the existing resume path (`_resumePayload` local merge + continue loop from the interrupt node's outgoing edges). Adds no new external deps, validates the contract, and gives consumers a fallback for non-MAF stacks.
4. **3-executor split per Interrupt node in the MAF adapter.** Interrupt node `{id}` expands into three MAF executors:
   - `{id}` — the existing `GraphNodeExecutor` modified with a `hitlPortId` parameter. When the port id is set and the body is not being skipped, emits `GraphInterruptedEvent`, stamps `ResumeFromNodeId = node.Id` on the message, and calls `context.SendMessageAsync(msg, "{id}_hitl")` instead of `YieldOutputAsync + RequestHaltAsync`.
   - `{id}_hitl` — a `RequestPort<GraphMessage, GraphMessage>` bound as a MAF executor via `BindAsExecutor()`. MAF pauses here and emits `RequestInfoEvent` with the forwarded `GraphMessage` as `ExternalRequest.Data`.
   - `{id}_hitl_resume` — another `GraphNodeExecutor` mapped to the **same** `GraphNode` but with `executorId = "{id}_hitl_resume"`. Because the incoming `GraphMessage.ResumeFromNodeId == node.Id`, `skipBody = true` fires and the executor evaluates outgoing edges and routes normally. No new node type, no duplication of predicate/effect logic.
5. **`InProcessExecution.OffThread` is required.** The MAF superstep loop blocks on the `RequestPortBinding` executor while the outer `WatchStreamAsync` enumeration needs to advance to emit `RequestInfoEvent`. Without `OffThread`, the executor blocks the single thread and `WatchStreamAsync` never yields — deadlock. `MafGraphOrchestrator.StreamWithHitlAsync` uses `InProcessExecution.OffThread.OpenStreamingAsync` + `TrySendMessageAsync` instead of `RunStreamingAsync`.
6. **`null` handler return = abort.** If `handleInterrupt` returns `null`, the orchestrator calls `run.CancelRunAsync()` (MAF) or `yield break` (InProcess), then emits `GraphFailed` with a `GraphHitlAbortedException`. This is analogous to `GraphRecursionException` for the `maxSteps` guard — a domain-specific exception callers can filter.
7. **HITL × checkpointer: both fire.** When a `checkpointer` is wired, the `{id}` emitter executor still saves a checkpoint before forwarding to the port (reusing the same path as the halt-mode Interrupt executor). Caller gets both: a live handler call AND a durable snapshot they can fall back to if the process crashes between `RequestInfoEvent` and `SendResponseAsync`. The checkpoint's `NextNodeId` is `"{id}_hitl_resume"` so the durable resume path re-enters at the routing step, not the body.

### Semantic difference vs. v0.9 HITL

| Dimension | v0.9 halt-mode (`IResumableAgentGraph`) | v0.42 live-mode (`IHitlAgentGraph`) |
|---|---|---|
| Workflow lifetime | Ends at interrupt; new workflow instance on resume | Single workflow stays open |
| Handler location | Called out-of-band (after `ResumeAsync`) | Called inline inside streaming enumeration |
| Durability requirement | Caller must persist `GraphCheckpoint` across invocations | Optional; checkpoint still fires if a `checkpointer` is wired |
| Best fit | Multi-day human approvals, cross-process boundaries | UI-in-loop, sub-second LLM routing decisions |

### Explicitly deferred

- **HTTP control-plane HITL.** `POST /v1/graphs/{runId}/respond` endpoint so a second HTTP call can feed a HITL response. Requires the `StreamingRun` to be held in a long-lived service (an Orleans grain or a `Channel`). Deferred to v0.10.
- **Typed HITL payloads.** `IHitlAgentGraph<TState, THitlRequest, THitlResponse>` with `GraphInterrupted<THitlRequest>`. The untyped `TState?` callback covers v0.42's use-cases. Deferred until a consumer asks for it.
- **Timeout / per-interrupt deadline.** `CancellationTokenSource` wired to each interrupt's handler call. Useful for SLA-bounded approvals. Deferred to v0.43 or alongside the HTTP control-plane variant.
- **Orleans durable HITL.** Grain-resident `StreamingRun` for cross-process resumption. Dependent on the HTTP control-plane endpoint above. Deferred.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Capability interface vs. merged into `IAgentGraph` | Capability interface (`IHitlAgentGraph<TState>`) | Orthogonal to run/stream semantics; mirrors `IResumableAgentGraph` precedent; future orchestrators that can't support live HITL don't stub |
| 2 | Callback signature | `Func<GraphInterrupted, CancellationToken, ValueTask<TState?>>` | `GraphInterrupted` is already the event consumers see on `StreamAsync`; `TState?` null-is-abort is the simplest typed result; `ValueTask` for zero-alloc hot paths |
| 3 | `null` return semantics | Abort: `GraphFailed` + `GraphHitlAbortedException` | Mirrors `maxSteps` guard; gives callers a typed exception to catch; `CancelRunAsync` cleans up the MAF run cleanly |
| 4 | `InProcess` scope | Implement both orchestrators | Symmetry validates the contract; InProcess consumers (SK stack, tests) don't need MAF for live HITL |
| 5 | MAF deadlock prevention | `InProcessExecution.OffThread` | Required: the RequestPortBinding executor blocks its thread while `WatchStreamAsync` needs to advance on the same thread. `OffThread` moves superstep execution off the caller's thread |
| 6 | HITL × checkpointer | Both fire | Gives callers redundancy without an explicit choice. The checkpoint's `NextNodeId = "{id}_hitl_resume"` so durable resume skips the interrupt body and enters the routing step |
| 7 | Resume-router executor id | `"{id}_hitl_resume"` | Distinct id avoids MAF executor-id collision; the executor is constructed with `executorId` override + same `GraphNode` so it shares predicate + edge logic with the emitter |
| 8 | Port → node-id mapping | `Dictionary<string, string> portIdToNodeId` built in `Build` | Port id = `$"{node.Id}_hitl"` by convention; the map is passed to `StreamWithHitlAsync` so `RequestInfoEvent.ExternalRequest.PortInfo` can be resolved back to the interrupt node id |

---

## Extensions to existing packages

No new packages. All changes land as additions in existing packages (zero breaking changes on shipped surface):

- **`Vais.Agents.Abstractions`** — `IHitlAgentGraph<TState>` + `GraphHitlAbortedException`.
- **`Vais.Agents.Core`** — `InProcessGraphOrchestrator<TState>` implements `IHitlAgentGraph<TState>`; `StreamWithHitlAsync` / `InvokeWithHitlAsync` inline-callback path.
- **`Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework`** — `GraphNodeExecutor` new `executorId` + `hitlPortId` ctor params; `MafGraphBuilder.Build` new `useHitl: bool = false` param (generates 3-executor split + port-map when true); `MafGraphOrchestrator<TState>` implements `IHitlAgentGraph<TState>` via `StreamWithHitlAsync` / `InvokeWithHitlAsync`.

---

## Delivery

### PR 1 — `IHitlAgentGraph<TState>` + `InProcessGraphOrchestrator` implementation

**Packages**: `Vais.Agents.Abstractions` (extend) + `Vais.Agents.Core` (extend).

Tasks:

- [x] `IHitlAgentGraph<TState>` in `Vais.Agents.Abstractions`:
  ```csharp
  public interface IHitlAgentGraph<TState>
  {
      IAsyncEnumerable<AgentGraphEvent> StreamWithHitlAsync(
          TState initial, AgentContext context,
          Func<GraphInterrupted, CancellationToken, ValueTask<TState?>> handleInterrupt,
          CancellationToken cancellationToken = default);

      ValueTask<TState> InvokeWithHitlAsync(
          TState initial, AgentContext context,
          Func<GraphInterrupted, CancellationToken, ValueTask<TState?>> handleInterrupt,
          CancellationToken cancellationToken = default);
  }
  ```
- [x] `GraphHitlAbortedException : Exception` in `Vais.Agents.Abstractions` — thrown when the handler returns `null`. Message: `"HITL handler returned null for interrupt '{nodeId}' — run aborted."`.
- [x] `InProcessGraphOrchestrator<TState>` implements `IHitlAgentGraph<TState>`. `RunAsync` gained optional `hitlHandler` parameter. Interrupt block: if handler is null → `yield break` (halt-mode, unchanged); if non-null → await handler, if null return → `GraphFailed` + throw `GraphHitlAbortedException`; if non-null → merge under `"hitl.response"`, emit `StateUpdated`, set `isResume = true` (triggers `skipNodeBody` on fall-through), continue to outgoing-edge evaluation. Multiple sequential interrupts supported.
- [x] `InvokeWithHitlAsync` — drains `StreamWithHitlAsync`; `GraphHitlAbortedException` propagates naturally from the iterator.
- [x] `PublicAPI.Unshipped.txt` updated for Abstractions and Core.
- [x] Tests — 4 new in `Vais.Agents.Core.Tests/InProcessGraphOrchestratorTests.cs`:
  - (13) Single interrupt, handler returns new state — graph continues past interrupt, `GraphInterrupted` + `GraphCompleted` both emitted, returned state reflects merged handler payload.
  - (14) Multiple sequential interrupts — handler called once per interrupt in order; all responses merged; final state reflects all handler payloads.
  - (15) Handler returns `null` — `GraphFailed` emitted with `GraphHitlAbortedException`; no `GraphCompleted`.
  - (16) `InvokeWithHitlAsync` — asserts returned `TState` matches expected final state after single-interrupt callback.

### PR 2 — MAF adapter HITL

**Packages**: `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` (extend).

Tasks:

- [x] `GraphNodeExecutor` — two new optional ctor params: `executorId?` + `hitlPortId?`. HITL interrupt branch: emits `GraphInterruptedEvent`, saves checkpoint (`NextNodeId = node.Id`), stamps `ResumeFromNodeId = node.Id`, `SendMessageAsync` to port. Halt-mode branch unchanged.
- [x] `MafGraphBuilder.BuildForHitl` added (existing `Build` unchanged). Returns `MafGraphBuildResult(Workflow, PortIdToNodeId)`. 3-executor split per Interrupt node; edges remapped; `WithOutputFrom` = End + resume routers.
- [x] `MafGraphOrchestrator<TState>` implements `IHitlAgentGraph<TState>`. `StreamWithHitlAsync` uses `InProcessExecution.OffThread.OpenStreamingAsync` + `TrySendMessageAsync`; loop buffers `GraphInterruptedEvent`, handles `RequestInfoEvent` inline (handler call + `SendResponseAsync` or `CancelRunAsync` + abort). `InvokeWithHitlAsync` thin wrapper.
- [x] `PublicAPI.Unshipped.txt` updated for the MAF adapter package.
- [x] Tests — 6 new in `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework.Tests/MafGraphOrchestratorTests.cs`:
  - (10) Single interrupt HITL — handler called with correct `GraphInterrupted` event; graph continues; `GraphCompleted` emitted; returned state reflects merged handler payload. ✓
  - (11) Multiple sequential interrupts HITL — two interrupt nodes; handler called twice in manifest order; both payloads merged into final state. ✓
  - (12) Handler returns `null` — `GraphFailed` with `GraphHitlAbortedException` emitted; run is cancelled; no `GraphCompleted`. ✓
  - (13) HITL parity with InProcess — same graph, same handler payload, both orchestrators produce the same final state. ✓
  - (14) HITL + checkpointer — interrupt checkpoint has `NextNodeId = "review"` (node.Id, not `{id}_hitl_resume`); graph completes and `IsComplete = true` checkpoint written. ✓
  - (15) `InvokeWithHitlAsync` MAF — asserts returned `TState` matches expected final state after single-interrupt callback. ✓

### PR 3 — API freeze + v0.42 cut

**Packages**: all affected (Abstractions, Core, Orchestration.Graph.MicrosoftAgentFramework) + milestone bookkeeping.

Tasks:

- [x] API freeze: `Unshipped` → `Shipped` for Abstractions (`IHitlAgentGraph<TState>`, `GraphHitlAbortedException`), Core (`StreamWithHitlAsync` / `InvokeWithHitlAsync` on `InProcessGraphOrchestrator`), MAF adapter (same two methods on `MafGraphOrchestrator`, `MafGraphBuildResult`, updated `MafGraphBuilder.Build` overloads).
- [x] Deferred-backlog entry for HITL / `RequestPort` struck in `docs/roadmap/deferred-backlog.md`.
- [x] Progress log entry appended to this plan.

---

## Exit criteria

- [ ] All 3 PRs landed on OSS repo `main`.
- [ ] 10 new tests (4 InProcess + 6 MAF) passing; full non-container suite green.
- [ ] `IHitlAgentGraph<TState>` is implemented by both `InProcessGraphOrchestrator<TState>` and `MafGraphOrchestrator<TState>`.
- [ ] The HITL path and the halt-mode path (`IResumableAgentGraph<TState>`) remain independent — no shared mutable state, no breaking change to existing `RunAsync` / `ResumeAsync` call sites.
- [ ] `MafGraphBuilder.Build` (no `useHitl` arg, existing callers) is unaffected — tested by the existing 15 tests all passing.
- [ ] `deferred-backlog.md` HITL item struck.

---

## Progress log

- 2026-04-25 — plan created after MAF API survey (`Microsoft.Agents.AI.Workflows 1.1.0` XML doc). 7 decisions locked; 3 PRs scoped; `RequestPort` / `StreamingRun` / `RequestInfoEvent` / `OffThread` APIs confirmed in DLL doc. **Pending**: PR 1 (`IHitlAgentGraph<TState>` + InProcess impl).
- 2026-04-25 — **PR 1 landed.** New files: `IHitlAgentGraph.cs` + `GraphHitlAbortedException.cs` in `Vais.Agents.Abstractions`. `InProcessGraphOrchestrator<TState>` now implements `IHitlAgentGraph<TState>` via `StreamWithHitlAsync` / `InvokeWithHitlAsync`. `RunAsync` gained a `hitlHandler` optional parameter (null = halt-mode, unchanged); interrupt block branches on whether handler is set: if null → `yield break` (existing halt-mode), if non-null → await handler, merge response under `"hitl.response"`, emit `StateUpdated`, set `isResume = true` to skip body on fall-through, then evaluate outgoing edges normally. Abort path: handler returns null → emit `GraphFailed(GraphHitlAbortedException)` + throw. PublicAPI.Unshipped updated for both packages. 4 new tests (single interrupt, multi-interrupt, null/abort, `InvokeWithHitlAsync`), all green. Full Core.Tests suite: 422 tests, all passing (+4). MAF adapter unaffected. **Pending**: PR 2 (MAF adapter HITL).
- 2026-04-26 — **PR 2 landed.** `GraphNodeExecutor` gained `executorId?` + `hitlPortId?` ctor params; interrupt block branches: HITL path stamps `ResumeFromNodeId = node.Id`, calls `SendMessageAsync` to port (no yield/halt); checkpoint `NextNodeId = node.Id` (crash-recovery compatible with `IResumableAgentGraph`). `MafGraphBuildResult` record struct added. `MafGraphBuilder.BuildForHitl` added: 3-executor split per Interrupt node (emitter → `RequestPort<GraphMessage,GraphMessage>` → resume router); manifest edges from interrupt nodes remapped to `{id}_hitl_resume`; `WithOutputFrom` = End nodes + resume routers. `MafGraphOrchestrator<TState>` now implements `IHitlAgentGraph<TState>`: `StreamWithHitlAsync` uses `InProcessExecution.OffThread.OpenStreamingAsync` + `TrySendMessageAsync`; `WatchStreamAsync` loop buffers `GraphInterruptedEvent` then handles `RequestInfoEvent` (calls handler inline, `SendResponseAsync` or `CancelRunAsync`). `PublicAPI.Unshipped.txt` updated. 6 new MAF HITL tests (single interrupt, multi-interrupt, null/abort, parity with InProcess, checkpointer, `InvokeWithHitlAsync`), all green. Full suite: 422 Core + 22 MAF + all other suites, 0 failures. **Pending**: PR 3 (API freeze).
- 2026-04-26 — **PR 3 landed.** API freeze: `Unshipped` → `Shipped` for all 3 packages (Abstractions: +60 entries; Core: +9 entries, 2 old ctor signatures removed; MAF adapter: +24 entries, 3 old signatures removed); all `Unshipped.txt` reset to `#nullable enable`. Deferred-backlog HITL / `RequestPort` item struck with `SHIPPED v0.42` annotation. Build clean (0 errors, 0 warnings). **Pillar v0.42 complete.**
