# v0.4.0-preview — Orchestration pillar (§9.7 of the architectural review)

Tactical plan for the seventh pillar. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §9.7. Created 2026-04-18.

---

## Scope

The review's §9.7 listed four items: `ITerminationCondition`, `Handoff` + `IHandoff`, `IAgentGraphExecutor` + `IAgentGraphBuilder`, and a `HandoffRequested` event. Post-design, shipping only the three that have real value without an implementation — graph interfaces are too design-speculative to ship as empty shells.

**Design decisions settled 2026-04-18**:

1. **Ship `ITerminationCondition`** — real value: async + composable + stateful-ready. Keep existing `TerminationPredicate` delegate shipped and add a `TerminationConditions.FromPredicate` adapter so consumers migrate gradually.
2. **Ship `Handoff` record + `HandoffRequested` event**. These are concrete data shapes that orchestrators and consumers need today regardless of implementation. `Handoff` carries `(FromAgent, ToAgent, Message?, HistoryToCarry?)`; `HandoffRequested` is the event-bus notification. Orleans surrogate + converter follows the M3e-3b pattern as always.
3. **Skip `IHandoff`**. The record IS the data contract; a parallel interface would duplicate the surface. If a concrete handoff-producing abstraction emerges from design-partner feedback (e.g., a participant self-hosting a handoff decision), we revisit.
4. **Skip `IAgentGraphExecutor` + `IAgentGraphBuilder`**. Too design-speculative without implementation — shipping empty interfaces pins design choices an eventual `GraphOrchestrator` might want to change. Consumers who need graph orchestration today implement `IAgentOrchestrator` directly; the graph interfaces land with `GraphOrchestrator` in a future milestone.
5. **`RoundRobinOrchestrator` gets an `ITerminationCondition` ctor overload** alongside the existing delegate one. Internal state stores `ITerminationCondition` — the delegate overload wraps via adapter. Forward-compatible.

---

## Delivery — single PR

**Packages**: `Vais2.Agents.Abstractions`, `Vais2.Agents.Core`, `Vais2.Agents.Hosting.Orleans`.

Tasks:

- [x] Abstractions: `ITerminationCondition.ShouldTerminateAsync(IReadOnlyList<OrchestrationStep>, CT) -> ValueTask<bool>`.
- [x] Core: static `TerminationConditions.FromPredicate(TerminationPredicate)` adapter — lives in Core (next to `TerminationPredicate`), not Abstractions.
- [x] Abstractions: `Handoff(FromAgent, ToAgent, Message?, HistoryToCarry?)` record.
- [x] Abstractions: `HandoffRequested(At, Context, Handoff)` event — 8th `AgentEvent` subclass.
- [x] Core: `RoundRobinOrchestrator` gains `(participants, maxRounds, ITerminationCondition?)` ctor; the existing delegate ctor chains to it via the adapter. Internal state stores `ITerminationCondition?`; `await condition.ShouldTerminateAsync(steps, ct)` replaces the sync call.
- [x] Hosting.Orleans: `AgentEventSurrogate` gains `HandoffFromAgent` + `HandoffToAgent` + `HandoffMessage` fields (Ids 19-21). `AgentEventKind.HandoffRequested = 7`. `HandoffRequestedSurrogateConverter`. `HistoryToCarry` omitted — not worth the list-of-records serialization complexity.
- [x] Tests — 9 new: 2 `TerminationConditionsTests` + 3 `RoundRobinOrchestrator` ITerminationCondition overload tests + 3 `HandoffRecordTests` + 1 Orleans round-trip.
- [x] `PublicAPI.Unshipped.txt` updates across three packages. 247/247 non-container green.

Breaking-change ledger: None. Pure additions. `RoundRobinOrchestrator` gets a new ctor overload; existing callsites compile unchanged.

---

## Progress log

- 2026-04-18 — plan created, five design decisions settled (ship ITerminationCondition, ship Handoff + HandoffRequested, skip IHandoff as redundant with record, skip graph interfaces as too speculative, RoundRobinOrchestrator ctor overload).
- 2026-04-18 — PR complete on local working tree. `ITerminationCondition` + `Handoff` + `HandoffRequested` in Abstractions. `TerminationConditions.FromPredicate` adapter + `RoundRobinOrchestrator` ITerminationCondition overload in Core. Orleans surrogate + converter for `HandoffRequested`. 9 new tests, 247/247 non-container green, 0 warnings.
