# Plan: comprehensive samples tree for `oss/agentic/samples/`

**Created:** 2026-04-19.
**Scope:** each public pillar and each shipped package gets at least one runnable consumer example. Each sample ≤ ~200 LoC, stands alone, runs with `dotnet run`, and doubles as a recipe referenced from the matching `docs/concepts/*.md` and `docs/guides/*.md`.

---

## 1. Research summary — current state

### 1.1 Existing consumer sample

| Sample | Lines | Demonstrates |
|---|---|---|
| `samples/HelloAgent/` | 145 | Stack-neutral `StatefulAiAgent` + tool-calling (`RollDiceTool`) across SK and MAF side-by-side. |

That's it. **One sample, three pillars exercised (session, execution-loop, tools), ten others missing.**

### 1.2 Internal probe (not consumer-facing)

| Probe | Lines | Coverage |
|---|---|---|
| `artifacts/smoketest/` | 249 | Touches all 13 packages, all pillars. Reflection-probe style — not a readable example. Not in `samples/`; gitignored for some assets. |

The smoketest is invaluable as a packaging check but doesn't teach the consumer anything. Its existence actually *hides* the sample gap: "look, everything's exercised!" — but only against types, not usage patterns.

### 1.3 Gap map (from docs-plan research)

| Pillar / feature | Runnable sample today? |
|---|---|
| Session | No (implicit in HelloAgent) |
| Memory (`IMemoryStore`) | No |
| Context provider + packer | No |
| Prompt composer | No |
| Guardrails (all 3 layers) | No |
| Execution loop (budget, interrupt) | No (basic loop visible in HelloAgent) |
| Tools (`Tool.FromFunc`, `IToolSource`) | Partial (HelloAgent does `ITool`) |
| Orchestration (Sequential / RoundRobin / Handoff) | No |
| Control plane (AgentManifest + registry) | No |
| Observability (OTel + Langfuse) | No |
| Orleans host | No |
| Redis / Postgres persistence | No |
| VectorData RAG | No |
| MCP outbound | No |
| A2A outbound | No |
| Tool-using streaming | No |

---

## 2. Decisions

### D1 — Granularity: one scenario per sample, ≤200 LoC

Alternatives: fewer "umbrella" samples (3-4 big ones) vs many small ones.

**Decision: many small ones, ≤200 LoC each.** Reasons: (a) a reader searching for "how do I add input guardrails" should find one file that shows only that; (b) each sample doubles as a self-contained recipe the `docs/guides/*.md` can point at without explaining "ignore lines 40-80, those are for another feature"; (c) keeps dep graphs per-sample minimal (only the packages needed). Tradeoff: more csproj files to maintain; acceptable cost.

### D2 — Project layout

```
oss/agentic/samples/
├── README.md                       # index of all samples
├── Directory.Build.props           # shared: net9.0, Nullable enable, no docs
├── HelloAgent/                     # existing — keep
├── HelloStreaming/                 # single-turn streaming basics
├── HelloStreamingTools/            # v0.4.1 tool-using streaming
├── CustomMemoryStore/              # IMemoryStore impl + inspection
├── ContextProviderRag/             # KnowledgeRetrievalContextProvider wiring
├── PromptComposer/                 # AggregatingSystemPromptComposer + contributors
├── InputOutputGuardrails/          # IInputGuardrail + IOutputGuardrail
├── ToolGuardrailsAndInterrupt/     # IToolGuardrail + AgentInterrupt HITL flow
├── BudgetEnforcement/              # RunBudget knobs in action
├── ToolFromFunc/                   # Tool.FromFunc<TIn,TOut> + IToolSource
├── SequentialOrchestration/        # SequentialOrchestrator
├── RoundRobinOrchestration/        # RoundRobinOrchestrator + termination predicate
├── HandoffBetweenAgents/           # Handoff + ITerminationCondition
├── OrleansSilo/                    # single-process silo + IAgentSessionGrain
├── OrleansRedisPersistence/        # Orleans + Redis clustering + storage
├── OrleansPostgresPersistence/     # Orleans + Postgres
├── VectorDataRag/                  # VectorData + IKnowledgeRetriever end-to-end
├── ObservabilityOtelConsole/       # OTel → console exporter + LangfuseEnrichmentFilter
├── McpToolSourceExample/           # outbound MCP wrap (McpToolSource)
├── A2ARemoteAgentExample/          # outbound A2A agent-as-tool (A2ARemoteAgentTool)
└── AgentManifestAndRegistry/       # InMemoryAgentRegistry + AgentManifest round-trip
```

**21 samples** (existing HelloAgent + 20 new). Phased rollout (see tasks).

### D3 — Each sample's contract

1. **One `.csproj`** referencing only the packages it needs (not the whole solution).
2. **One `Program.cs`** (or at most 2-3 files when the scenario needs auxiliaries) — executable `Main`, no web host, no DI container unless the pillar itself is DI-shaped (Orleans).
3. **One `README.md`** — 15-30 lines: what this shows, how to run, what output to expect, link back to the concept doc.
4. **Runs offline where possible.** Samples that genuinely need an LLM provider (HelloAgent, streaming samples) gate on `OPENAI_API_KEY` and fall back to a scripted fake provider so CI can still smoke-test them. Deterministic samples (guardrails, tools, orchestration, memory, registry) use an in-process fake provider exclusively.
5. **Dependencies via the local feed** `artifacts/packages/` (already wired via repo-local `NuGet.config` and the smoketest pattern).
6. **No hardcoded API keys, no hardcoded paths.** Read from env vars.

### D4 — Which samples need live LLM vs which are deterministic

**Live LLM (optional via env var)** — `HelloAgent`, `HelloStreaming`, `HelloStreamingTools`, `SequentialOrchestration`, `RoundRobinOrchestration`, `HandoffBetweenAgents`, `VectorDataRag`, `McpToolSourceExample` (against public demo servers if any).

**Deterministic (scripted fake provider)** — `CustomMemoryStore`, `ContextProviderRag` (retriever mocked), `PromptComposer`, `InputOutputGuardrails`, `ToolGuardrailsAndInterrupt`, `BudgetEnforcement`, `ToolFromFunc`, `OrleansSilo` (uses `FakeCompletionProvider` inside the silo), `OrleansRedisPersistence`, `OrleansPostgresPersistence`, `ObservabilityOtelConsole`, `A2ARemoteAgentExample` (against local fake A2A server or stubbed client), `AgentManifestAndRegistry`.

Reserve live-LLM samples for scenarios where the LLM behaviour is *the point*; every other sample uses a scripted provider so consumers can run the exact code without keys.

### D5 — Samples index README

`samples/README.md`: table with columns `[Sample | Pillar | Packages | LoC | Needs API key? | Concept doc]`. Readers browse the table, pick a row, copy the folder. Sorted by "learning path" (HelloAgent first, then pillar-by-pillar, then advanced).

### D6 — No Orleans-in-container sample shipping by default

The Orleans samples (`OrleansSilo`, `OrleansRedisPersistence`, `OrleansPostgresPersistence`) run **in-process** — the silo, the grain, and the client in the same `Main`. Redis / Postgres variants assume `docker-compose up redis postgres` started externally; include a `docker-compose.yml` in each sample folder. No Kubernetes, no Helm (that's Phase 3).

### D7 — Testcontainers in samples? No.

Persistence samples rely on the reader having Docker available with Redis / Postgres running on conventional ports. The Testcontainers path stays in test projects. A sample that orchestrates Testcontainers is 50% harness, which obscures the point.

---

## 3. Task list

### Phase 1 — Foundations (5 samples, no external deps)

- [ ] **S1: HelloAgent README + tidy.** Add a short README to the existing folder; no code changes unless the scrub plan touches it.
- [ ] **S2: `samples/Directory.Build.props`.** Shared `<TargetFramework>net9.0</TargetFramework>`, `<IsPackable>false</IsPackable>`, no docs, no public-API analyzer.
- [ ] **S3: `samples/README.md` skeleton.** Table with placeholder rows for all 21 samples.
- [ ] **S4: `PromptComposer`.** Demonstrate `AggregatingSystemPromptComposer` + two `ISystemPromptContributor` impls at different priorities, join output. Scripted fake provider.
- [ ] **S5: `CustomMemoryStore`.** Implement a trivial file-backed `IMemoryStore`, write + read + search. Print scoped behaviour across `MemoryScope.Session / Agent / Tenant`.
- [ ] **S6: `ContextProviderRag`.** Mock `IKnowledgeRetriever` in-process, wire `KnowledgeRetrievalContextProvider`, show the augmented `SystemPrompt` in the request that reaches the fake provider.
- [ ] **S7: `InputOutputGuardrails`.** Policy A on input (bans a word → Deny), policy B on output (redacts PII → Deny in v0.4; a future `Replacement` decision is post-v0.4). Catch `AgentGuardrailDeniedException`, print `TurnFailed` event.
- [ ] **S8: `ToolGuardrailsAndInterrupt`.** `IToolGuardrail` that returns `Interrupt` on a sensitive tool name; catch `AgentInterruptedException`, gather synthetic human input, call `ResumeAsync`. HITL loop in ~80 lines.
- [ ] **S9: `BudgetEnforcement`.** `RunBudget(MaxTurns: 2, MaxToolCalls: 3, MaxDuration: TimeSpan.FromSeconds(10))`. Two runs — one stays under, one trips each budget field. Print the `AgentBudgetExceededException.BudgetField`.
- [ ] **S10: `ToolFromFunc`.** Declare a handful of tools via `Tool.FromFunc<TIn, TOut>`. Show the auto-generated JSON schema. Wire an `IToolSource` that discovers tools dynamically (e.g., from a local dict).
- [ ] **S11: `AgentManifestAndRegistry`.** Build a full `AgentManifest` record, register via `InMemoryAgentRegistry`, look up by id, list by label prefix.

### Phase 2 — Streaming + orchestration (5 samples)

- [ ] **S12: `HelloStreaming`.** Basic `StatefulAiAgent.StreamAsync` with a scripted streaming provider — deltas printed character-by-character. Show the v0.4 filter + resilience limitation comment.
- [ ] **S13: `HelloStreamingTools`.** v0.4.1 tool-using streaming end-to-end with a scripted provider: preamble text → tool call → continuation text. Working-history / session-history split visualized in output.
- [ ] **S14: `SequentialOrchestration`.** Three `AgentParticipant`s piped; print each `OrchestrationStep`.
- [ ] **S15: `RoundRobinOrchestration`.** Two participants, `maxRounds=3`, `TerminationPredicate` that stops on a keyword. Print every step.
- [ ] **S16: `HandoffBetweenAgents`.** `Handoff` record, `ITerminationCondition` stops when a specific `HandoffRequested` event fires. Event bus consumer prints the handoff chain.

### Phase 3 — Hosting + persistence (3 samples, need Docker)

- [ ] **S17: `OrleansSilo`.** Single-process `TestCluster`-less silo using `Microsoft.Orleans.Hosting` extensions. `AddOrleansAgentRuntime` on the client side; `ConfigureAgentGrains` silo-side. Drive one turn through `OrleansAgentRuntime.GetSession` + `StatefulAiAgent`. Scripted fake provider.
- [ ] **S18: `OrleansRedisPersistence`.** Same as S17 plus `UseAgenticRedisClustering` + `AddAgenticRedisGrainStorage` + `UseAgenticRedisStreaming`. Includes `docker-compose.yml` with a Redis service.
- [ ] **S19: `OrleansPostgresPersistence`.** Same shape for Postgres. Includes `docker-compose.yml` + an `init-orleans-schema.sh` referencing Orleans' published SQL scripts.

### Phase 4 — Observability + RAG (2 samples)

- [ ] **S20: `ObservabilityOtelConsole`.** `AddAgenticOpenTelemetrySink` + `AddAgenticInstrumentation` on TracerProvider + MeterProvider. Console exporter for both. `AddLangfuseEnrichment`. Print spans + metrics during a scripted run. `agentic.*` tag catalogue visible in output.
- [ ] **S21: `VectorDataRag`.** SK `Connectors.InMemory` VectorStore + `SHA256`-based fake embedder (mirrors M3d test pattern). Ingest 3 docs → query → `KnowledgeRetrievalContextProvider` augments the system prompt. Can swap in real `Microsoft.Extensions.VectorData` providers.

### Phase 5 — Interop (2 samples)

- [ ] **S22: `McpToolSourceExample`.** Stand up a trivial local MCP server in-process (or point at a public demo), pre-connect an `McpClient`, wrap with `McpToolSource`, feed tools into `AggregatingToolRegistry.BuildAsync`. Scripted LLM provider drives one tool call.
- [ ] **S23: `A2ARemoteAgentExample`.** `A2ARemoteAgentTool.CreateAsync(uri)` against a local stub A2A server (included as a minimal `A2A.AspNetCore` boot script or a scripted `IA2AClient`). Driver agent delegates one sub-task to the remote. Demonstrates `AgentMessage` / `AgentTask` response shapes.

### Phase 6 — Verification + index

- [ ] **S24: `samples/README.md` populated.** Every sample appears with correct pillar / package / LoC / keys / concept-doc columns.
- [ ] **S25: Cross-reference sweep.** Every concept doc + guide links the matching sample(s); every sample's own README links back. Docs plan D24 link audit includes these.
- [ ] **S26: Run-all script.** `samples/run-all.ps1` (and `.sh`) that iterates every sample, builds, and runs the deterministic ones; skips the Docker-requiring ones unless `REQUIRE_DOCKER=1`. CI optional extension.
- [ ] **S27: CI smoke.** Add a `samples-build` matrix job to `.github/workflows/ci.yml` that builds every sample (doesn't run, just builds — catches package-reference drift).

---

## 4. Deferred / explicitly out of scope

- **ASP.NET Core integration sample** (web host + DI + agent as middleware) — post-1.0; web integration patterns are conventional.
- **Blazor / UI samples** — out of scope for a backend library.
- **Multi-region, multi-tenant deployment samples** — Phase 3 cloud runtime.
- **Graph-orchestration sample** — interface is deliberately deferred (§9.7); no sample until implementation lands.
- **MCP inbound sample (`McpAgentServer`)** — deferred per pillar plan.
- **A2A inbound sample (`A2AAgentEndpoint`)** — deferred per pillar plan.
- **Docker-image builds for each sample** — over-engineering; `dotnet run` is enough.

---

## 5. Exit criteria

1. 21 folders under `samples/` (existing HelloAgent + 20 new), each with `.csproj`, `Program.cs`, `README.md`.
2. `samples/README.md` index table lists every sample with correct metadata.
3. `samples/run-all.ps1` / `.sh` builds every sample clean.
4. Each sample runs to completion (deterministic ones unconditionally; live-LLM ones gated on env var).
5. Every sample referenced from at least one `docs/concepts/*.md` or `docs/guides/*.md`.
