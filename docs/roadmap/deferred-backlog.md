# Deferred backlog (Phase 0–3 consolidated)

## Purpose

This catalogue lists every item that was explicitly **deferred**, flagged as **out of scope**,
or kept as an **open question** during Phases 0 through 3 of the Vais.Agents OSS build-out
(v0.1 through v0.20-preview + Pillar F docs + samples). It is an input for Phase 4 roadmap
planning — not a prioritised plan. Entries are grouped by theme, dated, and cross-linked to
the plan document that originated them.

Everything here is a **conscious deferral**, not a hidden gap. Phase 3 shipped the
productised runtime end-to-end (docker-compose + Helm + operator + CLI + six samples); what
remains is enumerated below so Phase 4 triage can start from one list instead of rebuilding
it from 40+ pillar plans and a 1500-line milestone log.

## How it was assembled

Sources swept (2026-04-22):

1. `plans/actor-agents-oss-milestone-log.md` — every "**Deferred to …**", "Non-goals", "Next",
   "Follow-up", and "Drift" block from v0.3 through Pillar F. Richest source; entries are
   dated and authoritative.
2. `plans/actor-agents-oss-phase-3-runtime-productisation.md` — "Non-goals for Phase 3" and
   "Open questions to resolve in spikes".
3. Each pillar plan / findings / spike triplet under `plans/actor-agents-oss-v0.*-*.md` for
   the pillar-local "deferred to vX+1" clauses that didn't make it into the milestone log.
4. `plans/actor-agents-oss-architecture-review.md` and
   `plans/actor-agents-oss-dependency-upgrade-review.md` — cross-cutting items that were
   already categorised as low-urgency independent workstreams (e.g. .NET 10 multi-target,
   xUnit v3 migration, FluentAssertions licence pivot).

When you ship a new pillar and defer something, append a one-line bullet to the matching
theme below, keep the format consistent (**item** — Source link (date). Next step: …), and
update the Appendix dates if the Phase 3 non-goals change state.

---

## Themes

### 1. Identity & security

- **`IAgentIdentityProvider` implementations.** The contract shipped in v0.4 (§9.8); Phase 3
  kept it contract-only. Keycloak / Auth0 / Entra adapters never landed. Source:
  [Phase 3 plan §Non-goals](../../plans/actor-agents-oss-phase-3-runtime-productisation.md)
  + [milestone log v0.4 control plane](../../plans/actor-agents-oss-milestone-log.md) (2026-04-18).
  Next step: decide which IdP is first (Keycloak is the natural design-partner choice).
- **`ServiceAccountPrincipalMapper` runtime-side opt-in**: shipped but unwired by default.
  Source: same as above. Next step: add a Helm values toggle + a smoketest for the
  namespace-to-tenant mapping.
- **OPA bundle-server + signature verification.** Policies are loaded from disk / ConfigMap
  today; there is no signed-bundle pipeline. Source:
  [v0.14 OPA findings](../../plans/actor-agents-oss-v0.14-opa-policy-engine-findings.md)
  (2026-04-20). Next step: post-v0.14 polish pillar once a consumer asks.

### 2. Cross-runtime extensions

- **SSE streaming for cross-runtime invokes (`IAgentRemoteInvoker.StreamAsync`).** v0.20
  ships unary remote invokes only. Source: same (2026-04-21). Next step: v0.21 follow-up
  PR — stream via the v0.12 SSE client helpers.
- **`vais get-remote-runtimes` / runtime topology discovery.** Not in Pillar E or F.
  Source: same (2026-04-21). Next step: future CLI polish pillar.
- **Orleans streaming passthrough (`OrleansAiAgentProxy.StreamAsync`).** The Orleans
  streaming path returns 501 `urn:vais-agents:streaming-not-supported`. Source:
  [v0.12 SSE findings](../../plans/actor-agents-oss-v0.12-sse-streaming-invoke-findings.md)
  + [milestone v0.10 entry](../../plans/actor-agents-oss-milestone-log.md) (2026-04-20).
  Next step: future pillar once a consumer needs silo-spanning streaming.

### 3. Plugins & hosting

- ~~**Dynamic plugin hot-reload.**~~ v0.22 ships `ReloadPolicy.DrainAndSwap`: collectible
  `AssemblyLoadContext` per reload, `DefaultPluginReloader` with atomic registry swap,
  `FileSystemWatcher`-backed background watcher, `IPluginReloadHook` observer contract, and
  `TranslatorInvalidationHook` to clear the manifest-translator cache for affected agents.
  Source: [milestone v0.18 wrap-up §Deferred to v0.18.1](../../plans/actor-agents-oss-milestone-log.md)
  (2026-04-21) + [v0.22 pillar plan](../../plans/actor-agents-oss-v0.22-plugin-hot-reload-pillar.md).
  **SHIPPED v0.22**.
- ~~**Non-.NET plugins** (Python, Node, WASM / gRPC / stdio sidecars). v0.18 ABI is .NET-only.~~
  **PARTIALLY SHIPPED v0.23 (Python only — Node/Go/Rust remain deferred).** Python plugins ship
  as FastMCP stdio subprocesses managed by `IPythonPluginHost`; tools are contributed to the
  agent registry via `INamedToolSourceProvider`. Source: milestone log v0.18 (2026-04-21) +
  [v0.23 pillar plan](../../plans/actor-agents-oss-v0.23-python-plugins-pillar.md).
  Remaining: Node.js, Go, Rust, WASM sidecars — still deferred.
- **`vais plugins list` / `/v1/plugins` endpoint.** Plugin discovery is via startup logs
  only. Source: milestone log v0.18 (2026-04-21). Next step: small v0.18.x polish pillar
  once tagged.
- **HTTP control-plane `IManifestApplyDiagnosticsSink` implementation.** Sink contract +
  translator emission work today; no HTTP layer consumes it. Source: milestone log v0.18
  (2026-04-21). Next step: polish PR — thread warn records onto the `vais apply` response
  body.

### 4. Orchestration & graph

- **MAF `GraphNodeExecutor` durable resume parity.** The InProcess orchestrator resumes;
  the MAF adapter does not (MAF's own `CheckpointManager` uses a different checkpoint
  format). Source:
  [v0.9 findings + milestone entry](../../plans/actor-agents-oss-milestone-log.md)
  (2026-04-20). Next step: v0.10+ had it earmarked but it slid; revisit when graph resume
  gains a partner use case.
- **Custom declarable reducers in graph YAML.** v0.9 ships `LastWriteWins` + an
  `AppendMessages` reducer for the `messages` key. Custom reducers via YAML deferred.
  Source: milestone v0.9 (2026-04-20). Next step: future pillar when a partner brings a
  concrete non-default reducer.
- **Graph-level event-bus emission (`IAgentEventBus` inside orchestrators).** Consumers
  subscribe to `AgentEvent` via per-agent filters; the orchestrator itself does not
  publish. Source: milestone v0.9 (2026-04-20). Next step: design a concrete
  "orchestration step" event schema first.
- **Structural validator cross-checks on graph manifests.** `handlerRef.TypeName` +
  `stateBindings` ↔ `OutputSchema` cross-checks not run at apply time; runtime surfaces
  extraction mismatches. Source: milestone v0.9 (2026-04-20). Next step: loader-level
  validator sweep, probably alongside `manifest-schema.md` reference (see Docs).
- **HITL / `RequestPort`-backed MAF graph interrupts.** MAF graph adapter uses a simpler
  yield+halt pattern. Source: milestone v0.9 (2026-04-20). Next step: v0.10+ once MAF's
  checkpoint format stabilises.

### 5. Runtime container & operational polish

- **Chiseled base image flip (~98 MB).** Current image ~150 MB on Alpine. Source:
  [v0.16 runtime findings](../../plans/actor-agents-oss-v0.16-runtime-container-findings.md)
  + milestone log Pillar A (2026-04-21). Next step: Pillar F polish (CI image push to GHCR +
  signing + SBOM).
- **kind-based CI integration test.** v0.16 validates Helm chart rendering + compose config;
  no cluster-spinup test runs in CI. Source: milestone log Pillar A (2026-04-21). Next step:
  post-Phase-3 CI hardening pillar.
- **Multi-replica smoke test.** Documented in `deploy/compose/README.md` (`--scale
  runtime=3`) but not CI-automated. Source: milestone log Pillar A (2026-04-21). Next step:
  same CI hardening pillar.
- **Helm chart: image signing, SBOM, NetworkPolicy template, HPA template.** Source:
  milestone log Pillar A §Deferred to Pillar B–F (2026-04-21). Next step: post-Phase-3 prod
  hardening.
- **`Vais.Agents.Runtime.Host` as a published NuGet.** Source-only today (ships with the
  runtime container). Source: [AGENTS.md §1](../../AGENTS.md) + repo layout. Next step: only
  pack if a consumer asks to build a custom runtime host.
- **Redis streams alpha (`Microsoft.Orleans.Streaming.Redis 10.1.0-alpha.1`).** Runtime
  logs a startup warning on Postgres clustered-mode: no production Postgres stream provider
  in Orleans 10.x. Source:
  [dep-upgrade review](../../plans/actor-agents-oss-dependency-upgrade-review.md)
  (2026-04-18) + milestone log Pillar A. Next step: track upstream `Orleans.Streaming.Redis`
  for stable release; watch for a Postgres stream provider from the Orleans team.
- **Leader election / multi-replica HA for the operator.** Source:
  [v0.13 findings §Deferred to post-v0.13](../../plans/actor-agents-oss-v0.13-kubernetes-operator-findings.md)
  (2026-04-20). Next step: post-Phase-3 HA pillar.
- **In-process co-hosted operator (as `IHostedService` in the silo pod).** Source: same
  (2026-04-20). Next step: prove value first — today the two-process split is cleaner.
- **Public container image publishing to GHCR.** Repo ships the Dockerfile; users build
  their own. Source: same (2026-04-20). Next step: bundle with image signing + SBOM above.
- **Multi-version CR (`v1alpha1` + `v1beta1`).** Single-version today. Source: same
  (2026-04-20). Next step: when the CRD shape stabilises post-v1.0.
- **CRD schema tightening** — KubeOps 10.3.4 transpiler is TimeSpan-intolerant; `.spec` +
  `.status` use `x-kubernetes-preserve-unknown-fields: true`. Source: same (2026-04-20) +
  milestone v0.13. Next step: wait for upstream KubeOps fix or land operator-local mirror
  types (TimeSpan → ISO-8601 string).

### 6. CLI polish

Deferred post-v0.15 (explicitly documented scope-cuts in the CLI pillar):

- `vais audit` / audit-log query — needs a new HTTP endpoint (`GET /v1/audit`).
- `vais logs --runId <id>` journal replay — blocker: run-registry HTTP surface + AgentRun
  CRD.
- `vais describe <id>` (kubectl-shape detailed view).
- `vais port-forward`-equivalent.
- `vais top` (resource usage).
- OIDC device-flow auth (`vais auth login`).
- kubectl-style exec plugin (`users.<n>.exec: ...`).
- Shell completion (bash / zsh / fish / PowerShell).
- Standalone self-contained exe (single-file publish).
- Command aliases (`vais ls`, `vais rm`).
- `vais version --check` (remote NuGet version drift check).

Source: [v0.15 findings §Non-goals](../../plans/actor-agents-oss-v0.15-cli-findings.md) +
milestone v0.15 (2026-04-20). Next step: one polish pillar (v0.21–v0.22) if the design
partner asks for any two of these.

### 7. Documentation

Deferred to v0.17.1 / Pillar B polish (the declarative-agents tier):

- `docs/guides/ship-a-guardrail.md` — partner authoring reference for `IGuardrailFactory`.
- `docs/guides/ship-a-custom-model-provider.md` — partner authoring reference for
  `IModelProviderFactory`.
- `docs/reference/manifest-schema.md` — hand-written reference for the `AgentManifest` wire
  format; XML docs + `GET /openapi/v1.json` cover the gap today.

Source: [milestone v0.17 wrap-up](../../plans/actor-agents-oss-milestone-log.md)
(2026-04-21). Next step: v0.17.1 polish pillar or fold into Phase 4 docs pass.

- **Newcomer walkthrough (internal review).** Pillar F closed without an external reviewer
  running the 20-minute tutorial. Source:
  [Phase 3 plan Pillar F checklist](../../plans/actor-agents-oss-phase-3-runtime-productisation.md)
  (2026-04-21). Next step: first external design partner runs the tutorial; capture friction
  points as issues.
- **Rego authoring guide / style-guide doc.** Source:
  [v0.14 findings §Deferred](../../plans/actor-agents-oss-v0.14-opa-policy-engine-findings.md)
  (2026-04-20). Next step: post-v0.14 polish pillar.

### 8. Observability

- ~~**Per-attempt retry telemetry on the streaming pipeline.**~~ v0.21 adds
  per-attempt `stream_attempt` spans as children of the `chat` span. Each retry
  attempt in Phase 1 (enumerator-open + first MoveNextAsync) gets its own span
  with attempt index, status (Ok/Error), and error type tags. Source:
  [milestone v0.10 §Deferred](../../plans/actor-agents-oss-milestone-log.md)
  (2026-04-20). **SHIPPED v0.21**.
- ~~**Streaming journal replay.**~~ v0.21 adds `ReplayMode.Full` as an opt-in mode on
  `StatefulAgentOptions`; each `CompletionUpdate` delta is journaled as a
  `CompletionDeltaRecorded` entry (with a monotone `SequenceNumber`) and replayed
  verbatim on resume, bypassing the provider entirely. Tool outcomes are replayed from
  the existing `ToolCallRecorded` journal entries — no re-invocation on resume. Source:
  [milestone v0.10 §Deferred](../../plans/actor-agents-oss-milestone-log.md)
  (2026-04-20). **SHIPPED v0.21**.
- **Langfuse v3 local compose recipe.** v0.16 ships Langfuse v2 (v3's web + worker +
  clickhouse split is too heavy for dev). Partners wanting v3 fidelity run the Helm chart
  against a platform-team Langfuse. Source: milestone log Pillar A (2026-04-21). Next step:
  revisit when Langfuse v3 gets a leaner single-container dev distribution.
- **OPA decision-log forwarding for observability.** Not wired in v0.14. Source:
  [v0.14 findings](../../plans/actor-agents-oss-v0.14-opa-policy-engine-findings.md)
  (2026-04-20). Next step: post-v0.14 polish; pair with an OTel exporter target.
- **Custom operator metrics + traces beyond KubeOps defaults.** Source:
  [v0.13 findings §Deferred](../../plans/actor-agents-oss-v0.13-kubernetes-operator-findings.md)
  (2026-04-20). Next step: post-Phase-3 ops pillar.
- **Tool-invocation events in filters.** Surfacing per-adapter `FunctionInvokingChatClient`
  / SK auto-invoke hooks needs adapter-side work. Source:
  [milestone M3e entries](../../plans/actor-agents-oss-milestone-log.md) (2026-04-18).
  Next step: fold into an orchestrator-events pillar once streamed tool-call parity tests
  are in (see §4 orchestration above).

### 9. Persistence

- **Postgres streams provider (Orleans 10.x).** No production provider upstream; clustered +
  Postgres silently degrades to in-silo memory streams. Source:
  [milestone v0.16](../../plans/actor-agents-oss-milestone-log.md) (2026-04-21). Next step:
  monitor upstream Orleans releases; keep Redis as the documented default.
- **Additional VectorData sources** (Qdrant, Pinecone, Azure AI Search beyond InMemory +
  Postgres + Cosmos). Source:
  [architecture review](../../plans/actor-agents-oss-architecture-review.md) +
  [milestone M3d entry](../../plans/actor-agents-oss-milestone-log.md) (2026-04-18). Next
  step: partner-driven; the `KnowledgeRetrievalContextProvider` contract is stack-neutral.
- **Secret-value projection into `ModelSpec.ApiKeyRef` / `OutboundCredentialRef.Ref`.** v0.13
  resolves secrets but `ISecretResolver` rejects literals (URI-only); operator-resolved
  Secret values can't flow through the wire. Documented as a v0.13 limitation. Source:
  [v0.13 findings](../../plans/actor-agents-oss-v0.13-kubernetes-operator-findings.md)
  (2026-04-20). Next step: decide an inline-secret wire format (envelope extension) in a
  future pillar — cross-cuts operator + runtime.

### 10. Control plane (HTTP + idempotency)

Deferred post-v0.11 (OpenAPI + Idempotency pillar):

- **Swagger UI / Redoc bundling** — we publish the spec; consumers layer UI.
- **Client codegen from the spec** — consumers run Kiota / NSwag / `openapi-generator-cli`.
- **`RedisIdempotencyStore`** — InMemory covers dev, Orleans covers durable; Redis when
  asked.
- **Idempotency on non-HTTP inbound surfaces** (MCP tool calls, A2A tasks).
- **Full `ListTasksAsync`** — `OrleansTaskStore.ListTasksAsync` returns empty in v0.8;
  needs a separate index grain keyed by `ContextId`. Source:
  [milestone v0.8 entry](../../plans/actor-agents-oss-milestone-log.md) (2026-04-20).
  Next step: small follow-up PR once a consumer queries task listings.
- **Graph-level idempotency middleware**. The HTTP middleware scopes to `/v1/agents/…`;
  graph write verbs opt-in individually. Source:
  [milestone v0.11 §Deferred](../../plans/actor-agents-oss-milestone-log.md)
  + [v0.19 plan](../../plans/actor-agents-oss-v0.19-graph-as-deployable-findings.md)
  (2026-04-20/21). Next step: revisit once graph-invoke idempotency is requested.
- **Response header replay beyond status + content-type**. Stripe replays a safe-list of
  custom headers; Vais does not. Source: milestone v0.11 (2026-04-20).
- **Resume via `Last-Event-Id` on the SSE stream**. v0.12 treats mid-stream disconnect as
  a new turn. Source:
  [v0.12 findings](../../plans/actor-agents-oss-v0.12-sse-streaming-invoke-findings.md)
  (2026-04-20). Next step: decide whether stream-resume semantics go on the abstraction
  surface or on a hosted checkpointer.
- **Server-side event-bus fan-out endpoint** (cluster-wide observability stream). Source:
  same (2026-04-20). Next step: design after the `IAgentEventBus` topics story hardens.
- **OpenAPI schema emission for the SSE body.** The spec declares `text/event-stream`
  200 only; consumers doing client codegen need hand-authored SSE parsing. Source: same
  (2026-04-20). Next step: a small spec polish.
- **Streaming through `IAgentLifecycleManager` (policy + audit on SSE path).** Today
  streaming bypasses lifecycle manager. Source: same (2026-04-20). Next step: future
  pillar — add a streaming lifecycle verb.

### 11. OPA / policy polish

Deferred post-v0.14 (policy engine pillar):

- Embedded Wasm adapter (`Vais.Agents.Control.Policy.Opa.Wasm`).
- Envoy ext-authz gRPC adapter.
- Helm chart `opa:` sub-values block integrating the sidecar into
  `deploy/helm/vais-agents-operator/` (currently docs-only overlay).
- Rego linter / policy-CI tooling.
- Policy-version pinning via request headers (advanced safety).
- Bulk evaluation (batch multiple verbs per OPA call).
- Multi-engine composition helper (`CompositePolicyEngine`) — consumer concern.

Source: [v0.14 findings](../../plans/actor-agents-oss-v0.14-opa-policy-engine-findings.md)
+ milestone v0.14 (2026-04-20). Next step: a small polish pillar bundling the Helm
integration + decision-log forwarding (also in §Observability).

### 12. Testing & CI

- **xUnit v3 migration.** Independent workstream, flagged low-urgency. Source:
  [dep-upgrade review §Phase F](../../plans/actor-agents-oss-dependency-upgrade-review.md)
  (2026-04-18). Next step: run when FluentAssertions licence question is decided (they often
  touch the same test files).
- **FluentAssertions licence pivot.** Pinned at 6.12.2 (last MIT-licensed build); 7+
  requires commercial licence. Options: stay pinned forever, fork, or switch to
  `Shouldly` / hand-rolled asserts. Source: same (2026-04-18). Next step: decide before
  v1.0-preview cut.
- **`.NET 10` multi-target.** `Directory.Build.props` is `net9.0` only. Source: same
  (2026-04-18). Next step: after `.NET 10` ships stable and Orleans + KubeOps + MEAI have
  .NET 10 support.
- **Cross-host parity harness.** Hypothetical "same scenario produces byte-identical outputs
  on InMemory and Orleans hosts" test. Today's `CrossHostTests` exercises seams, not
  full-scenario parity. Source:
  [milestone M2c/M3e entries](../../plans/actor-agents-oss-milestone-log.md) (2026-04-18).
  Next step: once Redis persistence is in and streaming parity is locked.
- **Tool-call streaming parity test.** v0.10 parity suite proves text chunks align; does
  not drive a streaming tool-call flow (SK's fake `IChatCompletionService` can't exercise
  connector-level auto-invoke). Source: same (2026-04-18). Next step: land alongside the
  adapter-level dog-food / sample smoketests.
- **Automated kind-in-CI cluster tests for the operator.** Source:
  [v0.13 findings](../../plans/actor-agents-oss-v0.13-kubernetes-operator-findings.md)
  (2026-04-20). Next step: same CI hardening pillar as §5 runtime container.

### 13. Tag handling / release housekeeping

- **Tag motion for `v0.4.1-preview`.** `v0.4.0-preview` annotated tag still points at the
  API-freeze commit `9c73a4b`; post-tag repacked packages in `artifacts/packages/` reflect
  the MCP version bump. Tag motion deliberately deferred — decide whether to move,
  cut `v0.4.1-preview`, or land under a broader `v0.4.1`. Source:
  [milestone v0.4 post-freeze entries](../../plans/actor-agents-oss-milestone-log.md)
  (2026-04-18). Next step: triage at the next minor-version retrospective.
- **`v0.16.0-preview` tag application** — deferred to user confirmation (applied
  2026-04-21). Source: [milestone Pillar A wrap-up](../../plans/actor-agents-oss-milestone-log.md).
  Confirm the tag was pushed if/when the repo gains a remote.
- **`v0.17.0-preview` tag application** — same protocol; deferred to user confirmation.
  Source: [milestone Pillar B wrap-up](../../plans/actor-agents-oss-milestone-log.md).
  Same confirmation step.
- **Image signing + SBOM at every preview tag.** Not in Phase 3; folds into §5 runtime
  container CI hardening. Source:
  [Phase 3 plan Pillar A §Deferred](../../plans/actor-agents-oss-phase-3-runtime-productisation.md)
  (2026-04-21).

---

## Appendix A — Phase 3 non-goals (carried forward to post-Phase-3)

Straight from `actor-agents-oss-phase-3-runtime-productisation.md` §Non-goals
(2026-04-21); reproduced here for completeness.

- **Multi-region / leader-election.** Single-region, single-leader runtime only.
- **Identity-provider implementations.** See §1 Identity & security above.
- ~~**Dynamic plugin hot-reload.**~~ See §3 Plugins & hosting above. **SHIPPED v0.22**.
- ~~**Non-.NET plugins.**~~ See §3 Plugins & hosting above. **PARTIALLY SHIPPED v0.23 (Python only).**
- **Visual-designer / UI.** Dashboard out of scope. CLI + `kubectl` + Grafana are the
  surface.
- **Samples migration.** The housekeeping-samples plan stays deferred; Pillar F's
  end-to-end samples cover the same surface.

## Appendix B — Open questions still open post-Phase-3

From `actor-agents-oss-phase-3-runtime-productisation.md` §Open questions to resolve in
spikes — items that landed a decision during Phase 3 are omitted; what remains open:

- **Inline-secret wire format (operator-resolved values → runtime envelope).** Not decided.
  v0.13 stays URI-only. See §9 Persistence.

---

## Appendix C — How to add a new entry

1. Find (or create) the theme that best fits the deferred item.
2. Append a bullet: `**One-line item.** Source: [link](../../plans/…) (YYYY-MM-DD). Next
   step: …`.
3. If it's a cross-cutting polish pillar item that ties several bullets together, add a
   short umbrella paragraph under the theme rather than repeating the "next step" on every
   entry.
4. When an item ships, **delete it** from this file — this is a live backlog, not an
   archive. The milestone log keeps the historical record.
