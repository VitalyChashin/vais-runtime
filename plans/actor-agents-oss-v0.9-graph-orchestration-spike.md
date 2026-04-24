# v0.9 Graph orchestration — research spike

Scoped research pass before committing to a v0.9 pillar plan. Companion to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7 (backlog: "Graph orchestration implementation — `IAgentGraphExecutor` / `IAgentGraphBuilder` interfaces were deliberately deferred from §9.7 so the eventual `GraphOrchestrator` gets to shape them") and [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §4.1 ("Graph / DAG. Executors connected by typed edges with state passed between them. LangGraph, MAF Workflows, AutoGen GraphFlow. Our Sequential is a degenerate chain of this."). Created 2026-04-19.

---

## Why a spike before a pillar

v0.4 PR 7 (§9.7 orchestration) deliberately skipped `IAgentGraphExecutor` + `IAgentGraphBuilder` with this rationale: *"Too design-speculative without implementation — shipping empty interfaces pins design choices an eventual `GraphOrchestrator` might want to change."* That reasoning holds. The design space is wide enough that committing to a shape before doing real research would lock us into decisions we'd regret. A 1-2 day spike de-risks the pillar scoping before we burn pillar-length time.

The spike is **time-boxed research + a throwaway code experiment**, not a shippable package. Output: findings doc + sample YAML projections + throwaway `spike/agentic-graph-phase0/` code. No changes to the shipped library. No new public types added to `main`.

---

## Four blocking questions

1. **Q1 — Is MAF Workflows stable enough to wrap rather than reinvent?** Microsoft Agent Framework 1.1.0 ships a Workflows API (post-preview, GA-adjacent). If it's stable and its shape maps cleanly onto Vais' agent contracts, a thin adapter package is probably the right v0.9 move. If it's still churning or the shape is awkward, we'd ship a neutral `GraphOrchestrator` ourselves — same shape/cost as LangGraph/AutoGen's equivalents. **Blocker**: this decision cascades through the other three questions.

2. **Q2 — Do we need cycles + interrupt/resume in v0.9?** LangGraph allows cycles (ReAct loops, self-reflection) with checkpoint-based interrupt/resume. If we want those in v0.9, we commit to coupling graph state with v0.5's `IAgentJournal` for durable checkpointing. If we don't (DAG-only, no cycles, no human-in-the-loop pauses), the v0.9 surface stays much smaller and ships faster. **Decision axis**: how much of "durable agents" does v0.9 need to deliver vs. defer?

3. **Q3 — Typed state generic (`TState`) vs. shared-context bag?** LangGraph uses a typed generic state passed through the graph; AutoGen uses a shared message bag. .NET's generic story is strong, so typed state is idiomatic — but YAML projections break when the state type isn't JSON-schemable. **Decision axis**: developer ergonomics vs. declarative-ops story.

4. **Q4 — Declarative YAML viability?** If v0.6 ships `kind: Agent` manifests, `kind: AgentGraph` is a natural extension — same loader, same GitOps story, same "Kubernetes for agents" framing. But conditional edges and custom transformations are code-valued in LangGraph/MAF. Can we express 80% of realistic graphs declaratively without inventing a boolean/expression DSL? **Test**: draft two YAML archetypes and see where the declarative shape holds or breaks.

---

## Tasks (research + experiment)

- [x] **Q1 — MAF Workflows probe.** Delegated to research agent. Findings: `Microsoft.Agents.AI.Workflows` is a separate NuGet package, GA at 1.1.0 (2026-04-10). Public surface = `WorkflowBuilder`, `Executor<T>` (fully generic over any CLR type), `IWorkflowContext`, `RequestPort` (HITL), `InProcessExecution`, `CheckpointManager`. Cycles supported (just back-edges; type validation at build time). HITL via `RequestPort`. In-memory checkpoint built-in; durable needs custom `CheckpointManager` subclass. Sibling packages `Declarative` + `Hosting` still `rc1`/preview. See findings doc §Q1.
- [x] **Q2 — checkpoint story.** Delegated to research agent. Findings: LangGraph's `BaseCheckpointSaver` is a 4-method interface, thread-scoped, persists full state per super-step (not deltas), `interrupt()` raises runtime-caught exception and restarts the interrupting node on resume. Port-to-Orleans-journal is feasible — `thread_id` → `RunId`, super-step → journal entry. Determinism discipline is documentation, not runtime. See findings doc §Q2.
- [x] **Q3 — state model.** Synthesised locally from Q1/Q2 inputs. Decision: **hybrid** — `IAgentGraph<TState>` for code-first (matches MAF's generic executor shape) + `IAgentGraph` specialised over `IDictionary<string, JsonElement>` for declarative-first (supports YAML-authored graphs with JSON-schema state). Default reducer = last-write-wins + `appendMessages` well-known key. See findings doc §Q3.
- [x] **Q4 — YAML archetypes.** Drafted locally. **Archetype A (pure handoff)** projects trivially to YAML (Kubernetes-style `{property, operator, value}` matchers suffice). **Archetype B (retrieval loop)** projects with three specific declarative extensions: boolean combinators (`allOf`/`anyOf`/`not`), `OutputSchema`-driven state bindings on nodes (reusing v0.6's precedent), and a tiny edge side-effect vocabulary (`increment`/`set`/`append`) with `handlerRef` as the escape hatch. See findings doc §Q4.
- [ ] **Code spike.** ~~`spike/agentic-graph-phase0/` — throwaway .NET 9 project.~~ **Skipped.** Q1–Q4 collapsed the design space cleanly; the shape is unambiguous enough to proceed directly to pillar planning. If the pillar plan surfaces an integration concern, a focused code spike can address it then rather than now. Recorded in findings doc verdict.
- [x] **Findings doc.** [`actor-agents-oss-v0.9-graph-orchestration-findings.md`](./actor-agents-oss-v0.9-graph-orchestration-findings.md) — landed with Q1–Q4 synthesis + verdict (8 locked decisions + proposed 5-PR pillar shape + effort estimate).

---

## Exit criteria

- [x] All four questions answered with evidence (not opinion) — Q1/Q2 from background research agents with concrete API surfaces + doc links; Q3/Q4 synthesised locally with prior-art table + draft YAML.
- [x] Two sample YAMLs in the findings doc, with a paragraph each on "this shape holds / breaks here" — Archetype A (trivial) + Archetype B (stress test for declarative extensions).
- [ ] ~~Throwaway code spike builds and runs; findings doc links to the concrete files + friction points.~~ Skipped — verdict landed without needing a PoC. If a pillar-planning surprise emerges, a focused spike can run then.
- [x] Recommendation lands: **ready to write v0.9 pillar plan.** 8 decisions locked in the findings doc.

No public surface change. No package bumps. No tag.

---

## Progress log

- 2026-04-19 — spike plan created after design conversation. MAF-vs-build and declarative-extension both raised as design axes; all four questions scoped. **Pending**: Q1 through Q4 research + code spike + findings.
- 2026-04-19 — Spike complete. Q4 drafted locally (two YAML archetypes); Q1 (MAF Workflows) + Q2 (LangGraph checkpointer) delegated to parallel research agents, both returned solid evidence; Q3 (state model) synthesised locally. Findings doc landed with 8 locked decisions and a proposed 5-PR v0.9 pillar shape (1 new package `Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework` + extensions to 4 existing packages; 21 → 22 total). Code spike skipped — design space collapsed cleanly. **Ready to write v0.9 pillar plan.**
