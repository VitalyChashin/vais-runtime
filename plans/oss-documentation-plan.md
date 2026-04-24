# Plan: comprehensive documentation for `oss/agentic/` (pre-public docs pass)

**Created:** 2026-04-19.
**Scope:** every public-facing document in `oss/agentic/` — README, getting-started, per-pillar concept pages, per-package reference, guides, ADR index. Renders on GitHub as plain Markdown; a DocFX-generated API reference site can land later without rework.

---

## 1. Research summary — current state

### 1.1 What exists today

| Location | Purpose | Lines |
|---|---|---|
| `README.md` | Top-level overview: why / design principles / quick start / roadmap. | 70 |
| `CONTRIBUTING.md` | Contribution ground rules (stack-neutrality, PublicAPI, XML docs). | 28 |
| `CODE_OF_CONDUCT.md` | Contributor Covenant. | 32 |
| `SECURITY.md` | Pre-alpha, private vulnerability reports. | 11 |
| `LICENSE` | Apache 2.0. | 201 |
| `NOTICE` | Copyright stub. | 19 |
| `docs/adr/0001-keyed-ichatclient-di-convention.md` | Keyed-DI ADR. | 60 |
| `docs/adr/0002-otel-genai-conventions.md` | OpenTelemetry GenAI conventions ADR. | 56 |

**Total narrative docs: ≈214 lines** for ~8.4k LoC across 13 packages. No `docs/` content beyond the 2 ADRs.

### 1.2 XML-doc coverage (spot-checked)

- **Abstractions** (≈58 files, 594 shipped API entries): every public type sampled has `<summary>` + often extended `<remarks>`. Harness pillars' core types (`IAgentSession`, `IMemoryStore`, `IContextProvider`, `ISystemPromptComposer`, `IInputGuardrail`/`IOutputGuardrail`/`IToolGuardrail`, `RunBudget`, `IToolCallDispatcher`, `AgentInterrupt`) all well-documented.
- **Core**: entry-point types (`StatefulAiAgent`, `StatefulAgentOptions`) comprehensively documented; extended remarks on thread-safety, resilience, history ownership.
- **Smaller packages** (Persistence.Redis / .Postgres, Protocols.Mcp / .A2A): public surface is 3–11 entries each, all have summaries; behaviour depth is sparse by volume, not by quality.

XML coverage is fundamentally good. The API-reference gap isn't missing docs — it's missing a *surface* for readers to find them.

### 1.3 README coverage vs. pillars shipped

README mentions: `StatefulAiAgent`, Orleans in the roadmap, tools in the quick-start sample. README omits: session, memory, context provider, system-prompt composer, guardrails (any of three layers), execution-loop budget, orchestration, MCP, A2A, streaming. The roadmap is milestone-driven (M1/M2/M3/M4) rather than feature-driven, so readers can't map "what capability do I get from which package".

### 1.4 Discoverability

No `docs/INDEX.md`, no "which package for X?" table, no getting-started beyond "hello world".

---

## 2. Decisions

### D1 — Format: plain Markdown tree, GitHub-native rendering

Alternatives: DocFX (.NET-native, auto-generates API reference from XML docs, static-site build step), mkdocs / Docusaurus (richer UX, JS build step).

**Decision: plain Markdown tree under `docs/` for now.** Reasons: (a) readers opening the repo on GitHub see rendered content with zero build step; (b) XML-doc-driven API reference (DocFX) is a natural second step we can add without touching the hand-written guides; (c) defers the theming / hosting / deployment question until the repo is actually pushed publicly. Hand-written guides stay human-maintained; auto-reference comes later.

### D2 — Directory layout

```
oss/agentic/docs/
├── index.md                      # landing + table of contents
├── getting-started/
│   ├── installation.md           # NuGet.config + package picks per scenario
│   ├── hello-agent.md            # hello-world walkthrough (refs samples/HelloAgent)
│   └── choosing-a-stack.md       # SK vs MAF decision framework
├── concepts/
│   ├── architecture.md           # 13 packages layered diagram + dep graph
│   ├── session.md                # IAgentSession + IMemoryStore
│   ├── context.md                # IContextProvider + packer + merge rules
│   ├── prompt.md                 # ISystemPromptComposer + IPromptTemplate
│   ├── guardrails.md             # 3-layer split + decisions + events
│   ├── execution-loop.md         # RunBudget, dispatcher, interrupts, streaming-with-tools
│   ├── tools.md                  # ITool, IToolRegistry, IToolSource, Tool.FromFunc
│   ├── orchestration.md          # Sequential, RoundRobin, Handoff, ITerminationCondition
│   ├── control-plane.md          # AgentManifest, IAgentLifecycleManager, IAgentRegistry
│   ├── observability.md          # OTel GenAI conventions + Langfuse + events
│   ├── persistence.md            # Orleans + Redis + Postgres story
│   └── interop.md                # MCP + A2A outbound adapters
├── guides/
│   ├── wire-a-custom-tool.md
│   ├── add-input-output-guardrails.md
│   ├── run-on-orleans-locally.md
│   ├── add-redis-persistence.md
│   ├── add-postgres-persistence.md
│   ├── wire-rag-via-vectordata.md
│   ├── stream-with-tools.md
│   ├── expose-mcp-tools-to-an-agent.md
│   ├── delegate-to-a2a-remote-agent.md
│   └── deploy-otel-and-langfuse.md
├── reference/
│   ├── packages.md               # 13-package table: id, purpose, when-to-install
│   ├── events.md                 # AgentEvent closed hierarchy
│   ├── budget.md                 # RunBudget fields + enforcement points
│   └── telemetry-keys.md         # agentic.* OTel tags + Langfuse mappings
└── adr/
    ├── index.md                  # ADR index + status table
    ├── 0001-keyed-ichatclient-di-convention.md  (existing)
    └── 0002-otel-genai-conventions.md           (existing — update prefix per scrub plan)
```

Concept pages ≤ ~250 lines each; guides ≤ ~150 lines each; one Mermaid diagram per concept page where it clarifies flow. Reference pages are tables + one-liners.

### D3 — README rewrite

Trim to a tight top-level page: 1-paragraph pitch, feature matrix (11 features × packages), 15-line hello-world snippet, `docs/index.md` link as the entry to everything else. Target ≈120 lines. Remove the milestone-driven roadmap from the README; move roadmap prose into `docs/index.md` or a dedicated `docs/roadmap.md`.

### D4 — Mermaid diagrams

Embedded via ```` ```mermaid ```` fences (GitHub renders natively). Separate `.mmd` source files for the presentation deliverable (that's a different audience); docs diagrams stay inline for easy editing. Keep per-diagram under 40 nodes so they render on mobile.

### D5 — DocFX / API reference deferred

Add `docfx.json` + a build step only once the repo is published and a doc site is hosted. Meanwhile, XML docs are already complete — consumers can see them in IDE intellisense.

### D6 — Each concept page has the same skeleton

Purpose → Core types → Wiring (5-line code fence) → Extension points → Observability → Limitations / known gaps → "See also" links.

### D7 — Cross-reference between docs + samples + ADRs

Every concept page links to at least one runnable sample (per the samples plan), at least one ADR (where relevant), and at least one guide. Every guide links back to the concept. Every sample's README links back to the concept.

---

## 3. Task list

### Phase 1 — Foundation

- [ ] **D1: README rewrite.** Replace current 70-line README. Feature-matrix table. 15-line hello snippet. Points to `docs/index.md`. Apply scrub-plan T2 in the same commit.
- [ ] **D2: `docs/index.md`.** Landing page with "What / Why / For whom / Quick links" + the TOC tree above. Includes the 13-package-at-a-glance table.
- [ ] **D3: `docs/getting-started/installation.md`.** NuGet.config snippet for the local feed. Package-pick decision tree for common scenarios.
- [ ] **D4: `docs/getting-started/hello-agent.md`.** Walkthrough of `samples/HelloAgent`. Ends with "next: concepts/session".
- [ ] **D5: `docs/getting-started/choosing-a-stack.md`.** When to use SK, when MAF, what changes between them, the parity-finding history (SK auto-invoke is connector-level; MAF is pipeline-level).

### Phase 2 — Concepts (12 pages)

- [ ] **D6: `concepts/architecture.md`.** Layered diagram of all 13 packages + dep arrows. `Abstractions → Core → adapters → hosts → persistence → observability → protocols`. Call out InMemory vs. Orleans hosting paths.
- [ ] **D7: `concepts/session.md`.** `IAgentSession`, `InMemoryAgentSession`, `OrleansAgentSession`, grain-per-(agentId, sessionId) keying. Link to `run-on-orleans-locally` guide.
- [ ] **D8: `concepts/context.md`.** `IContextProvider` chain, merge rules, `IContextWindowPacker`, `KnowledgeRetrievalContextProvider`. Link to RAG guide.
- [ ] **D9: `concepts/prompt.md`.** `FormatStringPromptTemplate`, `AggregatingSystemPromptComposer`, `ISystemPromptContributor`, priority rule.
- [ ] **D10: `concepts/guardrails.md`.** 3-layer split (Input / Output / Tool). Pass / Deny / Interrupt. `AgentGuardrailDeniedException` + `InterruptRaised` event. Streaming post-facto semantics.
- [ ] **D11: `concepts/execution-loop.md`.** `RunBudget`, `DefaultToolCallDispatcher`, working-history / session-history split, `AgentInterrupt` + `ResumeAsync` (current shim + durable-execution plan). Tool-using streaming semantics. Mermaid sequence diagram.
- [ ] **D12: `concepts/tools.md`.** `ITool`, `IToolRegistry`, `IToolSource`, `AggregatingToolRegistry`, `Tool.FromFunc<TInput, TOutput>`, schema exporter caveats.
- [ ] **D13: `concepts/orchestration.md`.** `IAgentOrchestrator`, `SequentialOrchestrator`, `RoundRobinOrchestrator`, `Handoff`, `ITerminationCondition`, `HandoffRequested` event.
- [ ] **D14: `concepts/control-plane.md`.** 14 data records + `IAgentLifecycleManager` + `IAgentRegistry` + `InMemoryAgentRegistry`. Explicitly: cloud runtime (HTTP + CRDs) deferred to Phase 3.
- [ ] **D15: `concepts/observability.md`.** `AgenticDiagnostics` activity source, per-turn span, `OpenTelemetryUsageSink`, `LangfuseEnrichmentFilter`. Table of `agentic.*` tags (post-scrub values).
- [ ] **D16: `concepts/persistence.md`.** Orleans host, Redis grain-storage + streams, Postgres grain-storage, VectorData-backed RAG retriever. Which persistence layer affects which pillar.
- [ ] **D17: `concepts/interop.md`.** MCP outbound (`McpToolSource`), A2A outbound (`A2ARemoteAgentTool.CreateAsync`). Inbound deferred. Version pins (`ModelContextProtocol.Core 1.2.0`, `A2A 1.0.0-preview2`). TFM fallback note for A2A.

### Phase 3 — Guides (10 pages)

- [ ] **D18: Write 10 how-to guides** listed under D2's tree. Each: 5-minute read, runs against the matching sample, pastes the code that matters, links back to the concept.

### Phase 4 — Reference + ADR housekeeping

- [ ] **D19: `reference/packages.md`.** 13-row table: id, version, purpose one-liner, "install when…".
- [ ] **D20: `reference/events.md`.** 8-subclass `AgentEvent` hierarchy with per-event field table.
- [ ] **D21: `reference/budget.md`.** `RunBudget` fields + exactly where each is enforced (which turn-loop step, `AskAsync` vs `StreamAsync`).
- [ ] **D22: `reference/telemetry-keys.md`.** `agentic.*` OTel tag catalogue + Langfuse `langfuse.*` field mapping.
- [ ] **D23: `adr/index.md`.** ADR index page with status table. Update 0002 for the `agentic.*` prefix per the scrub plan.

### Phase 5 — Verification

- [ ] **D24: Link audit.** Every markdown link resolves. Every sample / ADR / reference cross-reference round-trips.
- [ ] **D25: Render audit.** Open the tree on a GitHub branch; verify every Mermaid diagram renders, every code fence has a language tag, every table has a header row.
- [ ] **D26: Spelling + style sweep.** Consistent tone (stack-neutral, factual), no breathless marketing copy. Scrub-plan T28 grep audit already confirmed zero `vais2` residue; concept/guide prose should mirror that.

---

## 4. Deferred / explicitly out of scope

- **DocFX API reference generation + hosted site** — post-public-push. XML docs already comprehensive; nothing lost.
- **Video walkthroughs / screencasts** — post-1.0.
- **Translations** — English-only until there's a design partner who needs otherwise.
- **Tutorials beyond the 10 guides** — add as consumers ask.

---

## 5. Exit criteria

1. `docs/` tree above exists with every listed file populated.
2. README trimmed + rewritten.
3. Every concept page links to ≥1 sample + ≥1 guide (where applicable).
4. Zero broken internal links, zero Mermaid render failures on GitHub.
5. The "new reader on GitHub opens the repo" path works: README → docs/index.md → getting-started → concepts → guides → samples.
