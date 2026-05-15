# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Version scheme: `0.X.0-preview` where X is the pillar number. Breaking changes are expected until the first tagged alpha.

---

## [Unreleased]

### Added

- **Sectioned LLM request composition (Phase 1, SC-1–SC-9).** The per-turn pipeline that produces a `CompletionRequest` now operates on typed `Section[]` instead of a three-slot `ContextContribution`. Each `Section` carries an Id (hierarchical, validated via `SectionId.Validate`), a `SectionKind` discriminator (`SystemSegment`/`UserMessage`/`AssistantMessage`/`ToolMessage`/`ToolDeclaration`/`ResponseFormat`/`Metadata`), a typed `SectionPayload`, optional `Order` + `ProducerId` + `SectionBudget` (priority 0–10 + per-section MaxChars). Wire shape at the `ICompletionProvider` boundary is byte-equal to v0.4; SK / MAF / OpenAI-compat adapters are unchanged.
  - **New pipeline:** `history-reducer → composer (Section[] via ComposeSectionsAsync) + base sections → IContextProvider chain (Section[] contributions) → ISectionResolver → ISectionWindowPacker → CompletionRequestFlattener → guardrails / filters / CompleteAsync`. `StatefulAgentOptions` gains `SectionResolver`, `SectionWindowPacker`, `SectionBudget` knobs; a legacy `IContextWindowPacker` is automatically wrapped in `LegacyPackerAdapter`.
  - **Default resolver invariants** (`DefaultSectionResolver`): no two sections share an id (`SectionCollisionException`); at most one `ResponseFormat` section per turn; canonical kind order `SystemSegment < {User/Assistant/Tool messages interleaved by Order} < ToolDeclaration < ResponseFormat < Metadata`; within a kind, explicit `Order` ascending, then null-`Order` sections in registration order.
  - **Default packer behaviour** (`DefaultSectionWindowPacker`): identity when no budget; greedy shed by descending `Budget.Priority` then descending size for over-budget cases. Priority 0 is critical and never dropped; default (no `SectionBudget`) is priority 5. Token-based sizing via optional `ITokenCounter`; chars otherwise. `Metadata` sections never count toward the wire budget.
  - **Legacy ctor preserved.** `ContextContribution(string?, IReadOnlyList<ChatTurn>?, IReadOnlyList<ITool>?)` still works — it emits Guid-suffixed sections (`system.legacy_addendum.<guid>`, `history.legacy_injected.<guid>.<i>`, `tools.legacy_additional.<guid>`) so multiple legacy providers don't collide on shared ids. The three legacy view properties (`SystemPromptAddendum`, `InjectedHistory`, `AdditionalTools`) are auto-derived from the section list for read-side back-compat; existing consumers (including `ContextInvocationContext.Candidate.SystemPrompt`) see the v0.4-shape string.
  - **Composer migration.** `ISystemPromptComposer` gains a `ComposeSectionsAsync` method (default impl wraps `ComposeAsync` as one `system.composed` section). `AggregatingSystemPromptComposer` overrides it to emit one SystemSegment per contributor (Order=Priority, Id=contributor.SectionId, ProducerId=contributor type name). `ISystemPromptContributor` gains a `SectionId` default-impl property returning `system.<type-name-kebab-case>`; override per-instance when registering multiple contributors of the same type.
  - **`KnowledgeRetrievalContextProvider` migrated.** Emits a `retrieval.docs` SystemSegment (Budget.Priority=5) instead of `SystemPromptAddendum`. `KnowledgeRetrievalOptions.SectionId` lets multi-retriever wirings disambiguate (e.g., `retrieval.support_kb`, `retrieval.product_specs`).
  - **Types added (Abstractions):** `Section`, `SectionKind`, `SectionPayload` (+ `TextPayload`, `TurnPayload`, `ToolsPayload`, `ResponseFormatPayload`, `MetadataPayload`), `SectionBudget`, `SectionId.Validate`, `ISectionResolver`, `SectionCollisionException`, `ISectionWindowPacker`, `SectionPackResult`, `PackerOutcome`, `SectionBudgetContext`, `PackerOutcomes`, `ITokenCounter`. **Core:** `DefaultSectionResolver`, `DefaultSectionWindowPacker`, `LegacyPackerAdapter`, `CompletionRequestFlattener`. **Plus:** `StatefulAgentOptions.{SectionResolver, SectionWindowPacker, SectionBudget}`, `ISystemPromptComposer.ComposeSectionsAsync`, `ISystemPromptContributor.SectionId`, `KnowledgeRetrievalOptions.SectionId`.
  - **Scope notes.** Observability wiring (Phase 2: OTel tags, Langfuse metadata, Prometheus metrics, RequestSectionsBuilt event, Grafana dashboard), the `wire-context-sections.md` tutorial (Phase 3), and the `POST /v1/sections/build` plugin endpoint + framework adapters (Phase 4) are tracked in `plans/sectioned-request-composition-impl-2026-05-15.md` and remain follow-on work. The `[Obsolete(DiagnosticId="VAIS0010")]` deprecation of the three-slot `ContextContribution` ctor is deferred until consumer migration completes — the legacy ctor stays warning-free for now.
  - **Research:** `research/sectioned-llm-request-composition-2026-05-15.md`. **Plan:** `plans/sectioned-request-composition-impl-2026-05-15.md`.

- **`agent:` tool source — local in-process agent delegation (P7, AAT-1–AAT-14).** A coordinator agent can now delegate to a sub-agent running in the same runtime as a first-class tool call, without an HTTP round-trip. The new `agent:<name>` source prefix is sibling to `static:`, `mcp:`, and `a2a:`.
  - **Blocking variant** — `LocalAgentTool` invokes the child synchronously and returns the reply. Session id is deterministic (`SHA256(runId + toolName + argHash)`); `AgentContext` propagates (`UserId`, `TenantId`, `WorkspaceId`, depth decrements); the session is removed after each call (stateless by default). Caller-supplied `sessionId` keeps the session alive for multi-turn sub-conversations.
  - **Background variant** — `BackgroundLocalAgentTool` starts the child as a fire-and-forget task and returns a JSON handle `{"handle":"…","status":"pending"}`. Three management tools are injected automatically: `list_background_agents`, `view_background_agent`, `cancel_background_agent`. Use `InMemoryBackgroundAgentTracker` (dev/test) or `OrleansBackgroundAgentTracker` (production, grain-backed, survives silo restart).
  - **Manifest shape** — declare sub-agents in `localAgents[]` and reference them from `tools[].source = "agent:<name>"`. `mode: Background` enables fire-and-forget. New fields: `agentId`, `agentVersion`, `mode`, `description`, `allowCallerSuppliedSession`, `propagateAllowedTools`.
  - **Depth guard** — `AgentContext.MaxChainDepth` prevents infinite delegation loops; the tool returns an error string when depth ≤ 0.
  - **Types added:** `LocalAgentRef`, `LocalAgentInvocationMode`, `LocalAgentTool`, `BackgroundLocalAgentTool`, `IBackgroundAgentTracker`, `BackgroundAgentRunRecord`, `BackgroundAgentRunStatus`, `InMemoryBackgroundAgentTracker`, `BackgroundAgentManagementTools`. Orleans types: `IBackgroundAgentRunGrain`, `IBackgroundAgentIndexGrain`, `BackgroundAgentRunGrain`, `BackgroundAgentIndexGrain`, `OrleansBackgroundAgentTracker`.
  - **Guide:** `docs/guides/delegate-to-a-local-agent.md` covers both modes, session lifecycle, context propagation, depth guard, and the P5 scaling contract table.
  - **Sample:** `samples/AgentAsToolDelegation/` — coordinator + math-specialist end-to-end example.

- **Option D — server-level `McpGatewayRef` inheritance.** When an agent omits `mcpGatewayRef`, the runtime now consults the `McpGatewayRef` of each bound `transport: registered` server. If exactly one distinct ref is found it is applied; if multiple distinct refs exist, translation fails with `urn:vais-agents:mcp-gateway-ref-ambiguous` (set an agent-level ref to disambiguate). Agent-level ref always takes precedence. `ManifestInstantiationUrns.McpGatewayRefAmbiguous` added. The `virtual-fetch.yaml` + `virtual-agent.yaml` pair in the `declarative-agent-mcp-gateways` sample serves as the canonical demo.

- **MCP virtual-server binding parity (VSB-1–VSB-5, VSB-6).** Binding a `transport: registered` MCP server in `mcpServers[]` now imports that server's full toolset without requiring any `tools[]` entries. This closes the consumption-model gap with IBM Context Forge's virtual-server model.
  - **VSB-1** — `AgentManifestTranslator.ResolveToolsAsync` gains an import-all path. When a `transport: registered` server has no `tools[]` entry referencing it (D1 presence-gated rule), every tool the server exposes is imported automatically. Physical servers are resolved via `INamedToolSourceProvider`; virtual servers use the pre-built `VirtualMcpToolSource`.
  - **VSB-2** — `McpServerRef.Tools` allowlist on the `mcpServers[]` entry narrows the import in import-all mode. Every allowlisted name must exist; absent names throw `urn:vais-agents:mcp-tool-not-found` at apply time.
  - **VSB-3** — Two import-all servers exposing the same tool name throw `urn:vais-agents:mcp-tool-name-collision` at apply time naming both servers. Explicit-vs-explicit collisions retain the existing first-wins behavior. Adding any explicit `tools[]` entry for one of the colliding servers switches it to explicit mode (D1), resolving the collision.
  - **Backward compatible** — every existing manifest that lists `tools[]` entries with `source: mcp:<name>` continues to work identically (explicit mode). The shipped `declarative-agent-mcp-gateways` sample is unchanged.
  - **Docs** — `wire-the-mcp-gateway.md` rewritten to lead with the one-line bind model; `gateway-config-control-plane.md` gains a two-axis explainer (`McpServer` = what tools, `McpGatewayConfig` = what policy).

- **OpenAI-compatible agent and graph gateway (OC-1–OC-12).** `Vais.Agents.Gateways.OpenAiCompat` now exposes registered agents and graphs through a standard `POST /v1/chat/completions` + `GET /v1/models` surface, enabling tools such as OpenWebUI, LiteLLM, and Continue.dev to talk to the Vais.Agents runtime without modification.
  - **OC-1** — `AgentInvocationRequest` gains an `InitialHistory` parameter (`IReadOnlyList<(string Role, string Content)>?`), enabling stateless multi-turn usage: each OpenAI call reseeds the agent session from the full message history, so edit and regenerate work correctly in any chat UI.
  - **OC-2** — `StatefulAiAgent.InvokeAsync(AgentInvocationRequest)` overload: when `InitialHistory` is non-empty the session is reset and the history turns are replayed before processing the new user message.
  - **OC-4** — `GET /v1/models` discovers `agent:<id>` entries from `IAgentRegistry` (optional) and `graph:<id>` entries from `IAgentGraphRegistry` (optional, opt-in only via the `vais.io/openai-compat-input-key` annotation).
  - **OC-5** — `POST /v1/chat/completions` routing fork: `agent:` prefix → agent path; `graph:` prefix → graph path; all other model IDs → existing `IModelRouter` path.
  - **OC-6** — Non-streaming agent dispatch: resolves `IAgentLifecycleManager`, seeds `InitialHistory` from all messages before the last user message, forwards `temperature`/`max_tokens`/`tools`/`tool_choice` as `oai.*` metadata keys.
  - **OC-7** — Streaming agent dispatch: resolves `IAgentRuntime`, uses session keyed by `X-Session-Id` header (or new GUID), emits SSE via `IStreamingAiAgent` event stream (`CompletionDelta`, `TurnCompleted`, `TurnFailed`, `GuardrailTriggered`); falls back to single-chunk SSE for non-streaming agents.
  - **OC-8** — Non-streaming graph dispatch: requires `vais.io/openai-compat-input-key` + `vais.io/openai-compat-output-key` annotations (returns `422` if absent); serializes messages as JSON input; extracts string or last-assistant-message output from `FinalState`.
  - **OC-9** — Streaming graph dispatch: same annotation requirement; emits SSE from `IAgentGraphLifecycleManager.InvokeStreamAsync` (`NodeAgentInvoked` → content chunks, `GraphCompleted` → stop, `GraphFailed` → error content).
  - **OC-10/OC-11** — `Vais.Agents.Control.Abstractions` added as project reference to both the gateway and its test project.
  - **OC-12** — QUICKSTART.md gains a new **"OpenAI-compatible gateway"** section covering `Program.cs` registration, agent and graph invocation examples, the opt-in annotation YAML, `X-Session-Id` multi-turn streaming, caller-param forwarding, and OpenWebUI connection instructions.
  - All new `IAgentRegistry`, `IAgentLifecycleManager`, `IAgentRuntime`, `IAgentGraphRegistry`, `IAgentGraphLifecycleManager` dependencies are optional (`GetService<T>()`), so existing deployments without these services registered continue to work.

### Changed

- **README positioning enhancements.** Three new sections added to `agentic/README.md`: a **"Why Vais.Agents?"** block (4 bold-lead bullets that implicitly position against durable-workflow runtimes, JS/Python agent frameworks, bare SK/MAF library use, and sidecar-style runtimes — without naming alternatives); an **"Examples"** gallery (3 collapsible `<details>` blocks showing a declarative agent with MCP tool, a multi-agent graph, and library-mode embed — the third absorbs the former standalone "Embed in a .NET app" section to avoid duplication); a **"Built on / interoperates with"** two-column table (.NET 9 · Orleans · MEAI · ASP.NET Core · KubeOps · Spectre.Console / MAF · SK · OpenAI · Anthropic · Azure OpenAI · MCP · A2A · OpenTelemetry · Langfuse · Prometheus · OPA · Redis · Postgres). README grew from 261 to 336 lines (+75 net); the existing "Embed in a .NET app" standalone section was removed in favour of the gallery's item 3.

### Removed

- **Docs cleanup (Phase 6 of the runtime-first docs reorg) — reorg complete.** Deleted three directories whose contents had been migrated or superseded by the Phase 3 audience sections: `docs/adr/` (6 files — decision history now lives in `CHANGELOG.md`, `plans/`, `research/`, and inline PR descriptions per the updated `GOVERNANCE.md`); `docs/getting-started/` (`installation.md`, `hello-agent.md`, `choosing-a-stack.md` had been re-shipped in `docs/library-mode/`; `deploy-your-first-agent.md` in `docs/agent-developer/`; `install-the-runtime.md` superseded by `docs/devops/deploy-runtime-on-docker.md`); `docs/tutorials/from-zero-to-graph-in-20-minutes.md` (re-shipped as `docs/agent-developer/compose-a-multi-agent-graph.md`). One file preserved across the move: `docs/getting-started/install-the-cli.md` → `docs/devops/install-the-cli.md` via `git mv` (operator-facing CLI install belongs with DevOps; history preserved). Bulk-updated 7 link-path patterns across all `.md` files (`getting-started/...` and `tutorials/from-zero-to-graph-in-20-minutes.md` now point at their new locations under `agent-developer/`, `devops/`, `library-mode/`). Surgically stripped 13 ADR cross-references across 9 files (concept pages, reference pages, guides). Updated `AGENTS.md` repository-layout + documentation-hierarchy descriptions to reflect the new audience sections. Updated `GOVERNANCE.md` to drop the `docs/adr/` directory reference and reframe "Architecture decisions" as inline PR / CHANGELOG / concept-page content. Updated `docs/contributing/ai-assistants.md` to remove the now-obsolete `/adr-new` command recipe and the ADR-diagram aside. **Runtime-first docs reorg complete across Phases 1–6.**

### Changed

- **Samples reorder (Phase 5 of the runtime-first docs reorg).** `samples/README.md` restructured: the single index table split into a **Runtime-first samples** table (14 rows — runtime container, declarative YAML, container plugins including the new Go + Python quickstarts, multi-agent graphs, K8s operator, CLI cookbook) leading the page, followed by a **Library-mode samples** table (~40 rows of `StatefulAiAgent` primitives, HTTP control-plane wiring, library-mode graph orchestration, etc.). Added `quickstart-python-planner` and `quickstart-go-plugin` to the index (they existed in the tree but weren't listed). Each table now has its own "learning path" subsection (the runtime-first 6-step path moves under the Runtime-first table; the library-mode path stays at 15 steps after dropping the "Phase 3 runtime path" step). All cross-reference doc links updated to point at Phase 3 tutorial pages (`docs/agent-developer/`, `docs/devops/`, `docs/deep-development/`, `docs/extensions/`, `docs/library-mode/`) instead of `getting-started/` and `tutorials/` pages that Phase 6 will delete. Stripped "v0.X Pillar Y" stamps from the feature column for consistency with the Phase 4 depillaring of the docs tree.
- **Depillaring sweep (Phase 4 of the runtime-first docs reorg).** Stripped "Pillar A/B/C/D/E/F" labels and "Phase 3" vocabulary from user-facing concept, reference, guide, and index pages: `docs/index.md` (concepts list parentheticals), `concepts/architecture.md` (7 references including 4 H2 section headers), `concepts/declarative-agents.md` (lead + 2 body), `concepts/runtime-plugins.md` (lead + 4 body + anchor link fix), `concepts/polyglot-plugins.md` (lead), `concepts/polyglot-agents.md` (lead + table headers + streaming row), `concepts/graph-as-deployable.md` (lead — also dropped the historical "Before v0.19" paragraph), `concepts/control-plane.md` (lead + 4 body), `reference/packages.md` (intro + plugin loader description + v0.16-v0.20 Pillar series), `reference/runtime-configuration.md` (lead + 4 inline + 2 composition-root rationale), `reference/problem-details-urns.md` (4 H3 section headings), `guides/author-an-agent-in-yaml.md` (lead + 2 body), `guides/install-the-runtime-locally.md` (image-build context + Langfuse note + deleted stale "501 you'll see on invoke" subsection + Known limitations bullet), `guides/deploy-the-runtime-to-kubernetes.md` (Langfuse note + Known limitations cleanup), `guides/package-an-agent-as-a-plugin.md` (lead), `docs/agent-developer/ship-a-python-agent.md` (corrected stale "coming in Phase 3" link to the now-shipped LangGraph tutorial). Version numbers preserved where they communicate compatibility (`(v0.17)`, `(v0.18)`, etc.); only the Pillar / Phase labels are removed. Internal-only surfaces (`docs/roadmap/deferred-backlog.md`, `docs/contributing/ai-assistants.md`) retain Pillar vocabulary by design — they're contributor-facing release-history artefacts. `docs/agent-developer/compose-a-multi-agent-graph.md` left untouched since it's slated for deletion in Phase 6.
- **Section 5 (Library mode) pages shipped (Phase 3e of the runtime-first docs reorg) — Phase 3 complete.** Three new pages under `docs/library-mode/`: `installation.md`, `hello-agent.md`, and `choose-your-stack.md`. Lifted from `library-mode/installation.md`, `library-mode/hello-agent.md`, and `library-mode/choose-your-stack.md` respectively, with library-mode framing in each lead ("Most users want Agent developer instead. This page is for the library path."), runtime-aware "Next" cross-links, and Pillar/version references in lead positions stripped (deeper depillaring deferred to Phase 4). Source pages in `getting-started/` stay in place until Phase 6 deletion. Section 5 landing page (`docs/library-mode/index.md`) updated to link the three in-section pages. **Phase 3 of the runtime-first docs reorg is now complete; remaining phases: 4 (depillaring concept openings), 5 (samples reorder), 6 (delete `docs/adr/`, `docs/getting-started/`, `docs/tutorials/`).**
- **Section 4 (Extensions) tutorials shipped (Phase 3d of the runtime-first docs reorg).** Three new pages under `docs/extensions/`: `author-an-llm-gateway-middleware.md` (authoring-shape walkthrough of `LlmGatewayMiddleware` subclassing with a `PromptInjectionGuardMiddleware` worked example demonstrating both non-streaming and streaming short-circuit), `author-an-mcp-gateway-middleware.md` (authoring-shape walkthrough of `ToolGatewayMiddleware` with a `ToolLatencyAlertMiddleware` observation pattern + a `TenantToolDenyMiddleware` short-circuit pattern), and `other-extension-seams.md` (catalog page covering 9 named seams — input middleware, guardrails, completion providers, session stores, history reducers, prompt composers, graph predicate operators, policy engines, event subscribers — with a "Picking the right seam" decision table). The Section 4 tutorials are authoring-shape (writing C# middleware classes) — distinct from the Section 1 LLM-gateway tutorial, which is consumer-shape (configuring middleware via YAML manifests). Section 4 landing page (`docs/extensions/index.md`) updated to link the three in-section pages.
- **Section 3 (Deep agent development) tutorials shipped (Phase 3c of the runtime-first docs reorg).** Three new tutorials under `docs/deep-development/`: `author-a-csharp-plugin.md` (reframing of `guides/package-an-agent-as-a-plugin.md`, depillared), `build-a-langgraph-plugin.md` (net-new from-scratch tutorial — minimal `classify → respond` LangGraph state graph, ~210 lines), and `author-a-container-plugin-in-go.md` (net-new lightweight — IP-1 HTTP protocol documented generically with Go as the worked example, ~230 lines). A new sample at `samples/quickstart-go-plugin/` provides the working artefacts (plugin.yaml, Dockerfile, go.mod, main.go, README — ~85 lines of Go, standard library only). Section 3 landing page (`docs/deep-development/index.md`) updated to link the new tutorials.

### Added

- **`samples/quickstart-go-plugin/`** — minimal container plugin in Go using only `net/http` and `encoding/json`. Implements the three IP-1 endpoints (`GET /health`, `GET /v1/metadata`, `POST /v1/invoke`) with a deterministic echo handler. Multi-stage Dockerfile produces a ~10 MB static binary on `scratch`. Companion sample for the [Author a container plugin in Go](docs/deep-development/author-a-container-plugin-in-go.md) tutorial.

### Changed

- **Section 2 (DevOps / admin) tutorials shipped (Phase 3b of the runtime-first docs reorg).** Six new tutorials under `docs/devops/`: `deploy-runtime-on-docker.md`, `deploy-runtime-on-kubernetes.md`, `add-redis-persistence.md`, `add-postgres-persistence.md`, `wire-langfuse.md`, and `wire-prometheus-and-grafana.md` (the net-new tutorial — scrape config + starter Grafana dashboard JSON + load-bearing `vais.*` metrics). Tutorials 1-2 lift from existing guides (`install-the-runtime-locally.md`, `deploy-the-runtime-to-kubernetes.md`) with stale Pillar / 501-on-invoke language stripped; tutorials 3-5 are operator-shaped reframings (env vars + Helm values) of guides that previously targeted library-mode silo configuration. Section 2 landing page (`docs/devops/index.md`) updated to link the new tutorials.
- **Section 1 (Agent developer) tutorials shipped (Phase 3a of the runtime-first docs reorg).** Five new tutorials under `docs/agent-developer/`: `your-first-declarative-agent.md`, `wire-the-llm-gateway.md`, `wire-the-mcp-gateway.md`, `ship-a-python-agent.md`, `compose-a-multi-agent-graph.md`. Reframe existing material with consistent "you'll build X" leads, prereq checklists, and end-state summaries. Source pages (`agent-developer/your-first-declarative-agent.md`, `agent-developer/compose-a-multi-agent-graph.md`) survive until Phase 6 cleanup; relevant `guides/` pages stay as depth references and are linked from each tutorial. Section 1 landing page (`docs/agent-developer/index.md`) updated to link the new tutorials.
- **Docs tree reorganized around audience and task (Phases 1-2 of the runtime-first docs reorg).** Five new top-level sections under `docs/`: `agent-developer/`, `devops/`, `deep-development/`, `extensions/`, `library-mode/`. Each is a curated landing page linking into existing `concepts/`, `guides/`, `tutorials/`, and `reference/` material — the underlying pages are unchanged in this pass. `docs/index.md` rewritten to lead with these five sections plus `Concepts` and `Reference`; the old "Getting started" enumeration, inline `Guides` catalog, `Architecture decisions` index, and "Package-to-pillar quick map" table have been removed from the index (the pages they referenced still exist and remain reachable via the section landings). `agentic/README.md` Documentation section expanded from a one-line cluster into the five section pointers. `samples/README.md` now leads with a "Runtime-first learning path" (docker-compose → declarative YAML → gateways → LangGraph Python → graph YAML → Kubernetes operator); the existing 16-step path is preserved as the "Library-mode learning path." Full plan in `plans/docs-samples-runtime-first-reorg-2026-05-13.md`; Phases 3-6 (write/curate tutorials, depillar concepts, samples reorder, delete ADRs + dissolved directories) are pending.

### Security

- **Container plugin host hardening (Phase 1 of plugin isolation contract / P12).** `DockerContainerSupervisor` now applies non-negotiable `HostConfig` defaults to every container plugin: read-only root filesystem, `/tmp` tmpfs (64 MiB), all Linux capabilities dropped, `no-new-privileges`, 256 MiB / 0.5 vCPU / 128 PIDs resource limits (overridable via `spec.resources` in the plugin manifest), and host port bind narrowed from `0.0.0.0` to `127.0.0.1`. The Kubernetes plugin Helm chart (`Charts/vais-plugin/`) gains a matching `securityContext` block — pod-level (`runAsNonRoot`, `runAsUser: 65532`, `seccompProfile: RuntimeDefault`) and container-level (`readOnlyRootFilesystem`, `allowPrivilegeEscalation: false`, `capabilities.drop: ALL`) — plus a Memory-backed `emptyDir` volume for `/tmp` (64 MiB).

  - **`spec.resources` in `plugin.yaml`.** Container plugins may now request up to operator-configured maxima (default: 2 GiB memory, 4 vCPU, 1024 PIDs) via the new optional `spec.resources` block. Absent fields fall back to the supervisor defaults. K8s-style quantity strings (`256Mi`, `0.5`, `500m`) are accepted.
  - **`vais plugin-init` Dockerfile template.** The generated `Dockerfile` for `--runtime dotnet` now includes `USER 65532:65532` before `EXPOSE`. New plugin authors get a non-root image by default.
  - **Migration note for existing plugins.** Any container plugin image that runs as root will still start under `DockerContainerSupervisor` (Docker only enforces `runAsNonRoot` at the K8s admission level). Add `USER <non-root>` to the plugin's `Dockerfile` to be K8s-compatible. Plugins that write outside `/tmp` will fail to start under the new read-only rootfs default — move writes to `/tmp` or pre-stage writable paths in the image.
  - Reference: `research/plugin-isolation-contract-2026-05-13.md`, `research/plugin-docker-isolation-2026-05-13.md`.

- **Container plugin egress isolation — internal-network mode (Phase 2 of P12).** Opt-in `--internal` Docker network topology that gives container plugins no NAT path to the internet. Enable by setting `VAIS_DOCKER_PLUGIN_NETWORK=<network-name>` on the runtime; the runtime and plugin containers share the named network and communicate via Docker embedded DNS instead of host-published ports.

  - **`DockerContainerSupervisor`** switches between two `HostConfig` branches: when `DockerPluginNetwork` is set, `NetworkMode` is set to the network name and `PortBindings` are omitted (plugin has no host port); otherwise the existing Phase 1 loopback port binding is used unchanged.
  - **`DockerNaming`** (internal helper) — single source of truth for the container name (`vais-plugin-{name}`) and invoke URL: `http://localhost:{port}` in legacy mode, `http://vais-plugin-{name}:{port}` in internal-network mode. Eliminates the previous dual URL-construction paths.
  - **`ContainerPluginLoaderOptions.PluginNetwork`** — new public property bound from `VAIS_DOCKER_PLUGIN_NETWORK`. Null/empty = legacy mode (default); any non-empty value activates internal-network mode.
  - **`local-dev/docker-compose.internal.yml`** — Compose overlay for `dev.ps1 -UseInternalNetwork`. Adds the `vais-internal` external network to the runtime service, mounts `/var/run/docker.sock`, and sets `VAIS_DOCKER_PLUGIN_NETWORK=vais-internal`.
  - **`local-dev/dev.ps1 -UseInternalNetwork`** — new switch. Creates `vais-internal` idempotently (`docker network create --internal`) before compose starts and applies the overlay.
  - **`deploy/demo-test.ps1 -UseInternalNetwork`** — same switch on the demo/integration test script.
  - **`tests/e2e/docker/run.ps1 -UseInternalNetwork`** — new mode in the E2E Docker suite. Creates a per-run `vais-e2e-plugin-net` internal network, starts the runtime as a container (on the default bridge for port publishing, then `docker network connect` to the internal net), and asserts that the plugin container has no published host ports and is attached to the internal network. All 13 assertions pass.
  - **`docs/guides/harden-docker-container-plugins.md`** — new production playbook covering both phases, daemon-wide hardening (`userns-remap`, rootless Docker, docker-socket-proxy roadmap), and a platform compatibility table (Linux / Docker Desktop).
  - Reference: `research/completed/plugin-docker-isolation-2026-05-13.md`.

### Fixed

- **Python plugin P12 outbound — gap closure (G0–G6 + Tavily tool-name fix).** Closes the follow-up gaps recorded in `plans/completed/python-plugin-p12-outbound-gaps-2026-05-13.md` and shipped via the impl plan `plans/completed/python-plugin-p12-outbound-gaps-impl-2026-05-15.md`. SGR analyst's full reasoning loop now runs end-to-end through the container gateway: `vais invoke sgr-analyst` returns multi-paragraph research output with citations drawn from live Tavily search results.

  - **G0 — `Dockerfile.demo` VOLUME mask.** Removed `VOLUME ["/var/lib/vais/plugins"]`. Docker created an anonymous volume on first run that persisted across every `compose up` and silently masked subsequent image rebuilds with stale in-image plugin DLLs. Existing operators need a one-time `docker compose down -v` to flush the stale volume.
  - **G3 — SGR analyst 401 against `/v1/container-gateway/chat/completions`.** `sgr-agent-core 0.7.0`'s `AgentFactory._create_client` builds `AsyncOpenAI(base_url=..., api_key=...)` without `default_headers`, so the gateway's HMAC validator (computes `HMAC(secret, X-Run-Id + X-Agent-Id)`) saw blank headers and rejected. `sgr-analyst/research.py` now subclasses `AgentFactory` and injects `default_headers={X-Run-Id, X-Agent-Id}` from a contextvars-scoped dictionary that `run_research` sets per invocation — async-task-isolated so concurrent invocations within one Python process don't cross-contaminate.
  - **G4 — Container gateway returns 415 for `stream=true`.** `HandleChatCompletionsAsync` no longer short-circuits streaming requests. Resolves `IStreamingCompletionProvider`, routes through `LlmGatewayPipeline.StreamAsync`, and writes SSE chunks via the new `ContainerGatewaySseWriter`. The non-streaming path also gains `LlmGatewayPipeline.InvokeAsync` (closes the `chat/completions` half of the G1 gateway-pipeline routing). Both paths push `AgentContext{RunId,AgentName}` from the request headers and deserialize OpenAI's `response_format: {type:"json_schema", json_schema:{...}}` into `CompletionRequest.ResponseFormat`. New types in `ContainerProtocol.cs`: `OpenAiResponseFormat`, `OpenAiJsonSchema`, `OpenAiChatChunk`, `OpenAiChatChunkChoice`, `OpenAiChatDelta`. All internal.
  - **G5 — `ResponseFormatSpec.Strict` dropped at the MAF/MEAI boundary.** MEAI 10.x's `ChatResponseFormat.ForJsonSchema(schema, name, description)` exposes no strict parameter — `Strict` was silently lost, so OpenAI ran structured outputs in best-effort mode and the model omitted required schema fields (e.g. SGR's `reasoning_steps`). `MafCompletionProvider.CompleteAsync` and `StreamAsync` now also set `chatOptions.AdditionalProperties["strict"] = rf.Strict`, which `MEAI.OpenAI.OpenAIChatClient.ToOpenAIChatResponseFormat` reads via `OpenAIClientExtensions.HasStrict` and passes to `OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat(..., jsonSchemaIsStrict:true)`. `ResponseFormatSpec.Strict` defaults to `true`, so existing callers get strict mode for free — matching the M1–M4 work's original intent.
  - **G6 — Container gateway drops `tool_calls` / `tool_call_id` on history messages.** `HandleChatCompletionsAsync` previously translated each `OpenAiChatMessage` to a `ChatTurn(role, content)`. Once G5 unblocked multi-turn SGR reasoning, any conversation with `assistant{tool_calls:[...]}` followed by `tool{tool_call_id:...}` was rejected by OpenAI with `400 invalid_request_error: messages with role 'tool' must follow assistant with tool_calls`. `OpenAiChatMessage` now carries optional `tool_calls` (array of `{id, type:"function", function:{name, arguments}}`) and `tool_call_id`. A new `OpenAiMessageToChatTurn` helper parses `function.arguments` (a JSON-encoded string at OpenAI protocol level) into a `JsonElement` and forwards the link to `ChatTurn`. Multi-turn-with-tools transport through the container gateway now works for any plugin, not just SGR.
  - **G1 (tail) — `HandleLlmCompleteAsync` routed through `LlmGatewayPipeline.InvokeAsync`.** Mirrors the chat/completions G4 wiring: injects `IEnumerable<LlmGatewayMiddleware>`, pushes `AgentContext` from headers. Plugin `request.llm.complete()` calls now produce `vais_gateway_events` rows on the same shape chat/completions does.
  - **G2 — `HandleToolInvokeAsync` routed through `DefaultToolCallDispatcher`.** Tool calls from container plugins now traverse `IToolGuardrail` (Before + After), append a `ToolCallRecorded` to `IAgentJournal` keyed by the request's `X-Run-Id`, emit `ToolCallStarted` / `ToolCallCompleted` on `IAgentEventBus`, and traverse the `ToolGatewayMiddleware` chain — same path C# agents take via `StatefulAiAgent`. A small private `SingleToolRegistry` adapts the resolved `ITool` to the `IToolRegistry` shape `DefaultToolCallDispatcher` expects. `AgentGuardrailDeniedException` is caught and surfaced as `IsError=true` in the response.
  - **`samples/PluginAgentResearchPipeline/sgr-analyst/src/sgr_analyst/research.py` — Tavily tool name.** `_GatewayWebSearchTool` was sending `toolName: "tavily_search"`, but the Tavily MCP server at `samples/PluginAgentLangGraphResearcherLive/tavily-mcp-server/server.py:11` registers its tool as `search` (FastMCP uses the Python function name verbatim — `"tavily-search"` at line 6 is the *server* name, not the tool name). With the name mismatch the gateway returned `Tool 'tavily_search' not found`, SGR's reasoning loop concluded data sources were unreachable, and the analyst produced the fallback `"unable to provide ... due to technical issues"`. After `tavily_search` → `search`, the same query returns a multi-point analysis with `[1]–[7]` citations from real Tavily results.
  - **Tests.** New `ContainerGatewayEndpointTests` + `ContainerGatewayToolInvokeTests` (7 cases) cover: non-streaming chat/completions middleware traversal + `RunId` propagation; streaming SSE chunks + final-chunk `finish_reason=stop` + `[DONE]`; `llm/complete` middleware traversal; missing-bearer 401; `tools/invoke` guardrail Before/After + journal append keyed by `RunId` + `ToolGatewayMiddleware` chain; guardrail Deny short-circuit; unknown tool returning `IsError`. All 100 tests in `Vais.Agents.Runtime.Plugins.Container.Tests` pass (93 existing + 7 new).

- **`IContainerPluginRegistry` missing from composition root.** `CompositionRoot` now calls `AddOrleansContainerPluginRegistry()` alongside the other pillar registries. Previously, `POST /v1/container-plugins` returned 500 because the Orleans-backed registry grain was never wired into DI.
- **E2E Docker suite (`tests/e2e/docker/run.ps1`).** Two fixes: `VAIS_CONTAINER_PLUGINS_DIRECTORY` is now set to a non-empty temp path (so `AddContainerPlugins` registers `IContainerPluginLifecycleManager`), and a pre-run `docker rm -f` removes stale plugin containers from aborted prior runs.
- **E2E Docker suite `-UseInternalNetwork` mode.** Three bugs fixed after first-run validation: (1) `--user root` added to the runtime `docker run` — the image's default user (`uid=65532`) cannot access `/var/run/docker.sock` (`root:root 0660`); (2) container port corrected from `$RuntimePort` to `8080` — the image has `ASPNETCORE_HTTP_PORTS=8080` baked via Kestrel, which takes precedence over `ASPNETCORE_URLS`; (3) `Get-Member` result wrapped in `@(...)` so `.Count` works when `PortBindings` deserializes to an empty `PSCustomObject`. All 13 assertions now pass (11 shared with legacy mode + 2 internal-network-specific).

### Added

- **CLI diagnostics command group `vais diagnose`** — live inspection of in-process OTel spans and Orleans grain call counters without container log archaeology.

  - `DiagSpanBuffer : BaseExporter<Activity>, IDiagSpanBuffer` (`src/Vais.Agents.Runtime.Host/Diagnostics/`) — opt-in circular span buffer (default capacity 1000). Registered in the OTel tracing pipeline and as `IDiagSpanBuffer` in DI when `VAIS_DIAG_SPAN_BUFFER=true`. Uses `SimpleActivityExportProcessor` so export is synchronous per span.
  - `FilterStatusTracker : IFilterStatusTracker` (`src/Vais.Agents.Runtime.Host/Diagnostics/`) — lock-free per-interface call counter incremented by `OrleansOutgoingActivityFilter`. Always registered (lightweight). Snapshot is ordered by total-calls descending.
  - `IDiagSpanBuffer`, `DiagSpanRecord`, `IFilterStatusTracker`, `FilterCallEntry` — new public contracts in `Vais.Agents.Control.Abstractions`.
  - `GET /v1/diagnostics/spans?source=&limit=` — returns recent spans from the buffer as `DiagSpanListResponse`. Returns 503 when buffer is not enabled (`VAIS_DIAG_SPAN_BUFFER` not set). Guarded by the standard JWT / OPA policy.
  - `GET /v1/diagnostics/filter-status` — returns `FilterStatusResponse` with per-interface `(WithActivity, WithoutActivity)` counters.
  - `vais diagnose spans [--tail N] [--source <ActivitySource>]` — fetches recent spans and emits NDJSON (one span per line), pipeable to `jq`.
  - `vais diagnose trace <traceId>` — fetches all spans for a trace, reconstructs and pretty-prints the span tree via Spectre.Console.
  - `vais diagnose filter-status [-o table|json]` — shows per-interface grain call counters in a Spectre table.
  - `OrleansOutgoingActivityFilter` — now emits `vais.grain.outgoing.calls` OTel counter (tagged `grain.interface`, `has_activity`) and calls `IFilterStatusTracker.RecordCall` on every outgoing grain call.
  - `IAgentControlPlaneClient` — new default methods `GetDiagSpansAsync` and `GetFilterStatusAsync`; concrete implementation in `AgentControlPlaneClient`.

- **`samples/IdentityOidc/`** (v0.29 + v0.30) — YAML-only sample showing cross-runtime JWT authentication with `VAIS_JWT_AUTHORITY`, `VAIS_SA_PRINCIPAL_MAPPER`, and all three `IdentityMode` options (`Forward`, `ServiceAccount`, `TokenExchange`). Includes `callee-agent.yaml`, `caller-agent.yaml`, `caller-graph.yaml`, and a full quickstart README.

- **Physical MCP server connections** (`src/Vais.Agents.Control.Mcp/`). New `Vais.Agents.Control.Mcp` package bridges `IMcpServerRegistry` physical entries to live `McpClient` connections, closing the gap where `transport:registered` servers were silently missing from agent tool registries.

  - `PhysicalMcpConnectionService : BackgroundService, INamedToolSourceProvider` — scans `IMcpServerRegistry` at startup and connects to every physical `streamableHttp` and `sse` server. Connections are fire-and-forget; the service re-enters a 30-second `PeriodicTimer` reconnect loop after the initial scan. On reconnect, registered `IMcpServerConnectionChangedHook` implementations are invoked so affected agent translator caches can be invalidated. Exposes connected servers to `AgentManifestTranslator` via `INamedToolSourceProvider.GetByName`. Scaling contract (P5): connections are per-silo; a server reachable from silo A but not silo B returns null on silo B, surfacing as `McpServerUnavailable` at agent activation.
  - `AddPhysicalMcpServers(this IServiceCollection)` — registers `PhysicalMcpConnectionService` as `IHostedService` + `INamedToolSourceProvider`. Requires `IMcpServerRegistry` and optionally picks up `IMcpServerConnectionChangedHook` implementations.
  - `IMcpServerConnectionChangedHook` (new interface in `Vais.Agents.Control.Abstractions`) — observer called after a server connects or disconnects. Exceptions are logged and swallowed; state is committed before hooks run.
  - `McpTranslatorInvalidationHook : IMcpServerConnectionChangedHook` (in `Vais.Agents.Runtime.Instantiation`) — on connect or disconnect, walks `IAgentRegistry` and calls `IAgentManifestTranslator.InvalidateAsync` for every agent that references the affected server via `transport:registered`. Registered automatically by `AddAgentManifestInstantiator`. Resolves the v0.22 `TranslatorInvalidationHook` TODO for physical MCP server reconnections.
  - `AgentManifestTranslator` — TODO comments replaced; the physical-server path is now fully served by `PhysicalMcpConnectionService` without any translator changes.

  **Note:** `SseClientTransport` does not exist as a standalone class in `ModelContextProtocol.Core` 1.2.0. SSE is handled via `HttpClientTransport` with `TransportMode = HttpTransportMode.Sse`. Auth (`AuthRef`/`secret://`) and `stdio` transport are deferred to a follow-up.

### Added (OSS repository)

- **Standard OSS repository scaffolding.** Adds the governance and automation files expected by GitHub-hosted open-source projects.

  - `GOVERNANCE.md` — benevolent-dictator governance model for Phase 1 (pre-alpha): roles (maintainer / contributor), decision paths by change type, and a pointer to `docs/roadmap/deferred-backlog.md` for out-of-scope work.
  - `.github/CODEOWNERS` — file-level ownership routing for GitHub review assignment.
  - `.github/ISSUE_TEMPLATE/` — structured bug-report and feature-request issue forms (`bug_report.md`, `feature_request.md`, `config.yml`).
  - `.github/PULL_REQUEST_TEMPLATE.md` — PR checklist (description, test plan, breaking-change checklist, changelog reminder).
  - `.github/dependabot.yml` — weekly Dependabot updates for NuGet and GitHub Actions.
  - `.github/workflows/codeql.yml` — CodeQL static-analysis workflow (C#, scheduled weekly + on push/PR to `main`).

### Fixed

- **`PhysicalMcpConnectionService` circular DI dependency** — a cycle between `PhysicalMcpConnectionService → McpTranslatorInvalidationHook → AgentManifestTranslator → INamedToolSourceProvider → PhysicalMcpConnectionService` prevented `GenericWebHostService` (Kestrel) from starting, silently leaving the HTTP control plane unbound while Orleans and OTEL continued running. Fixed by adding a second internal constructor that accepts `IServiceProvider` so hooks are resolved lazily at dispatch time rather than at construction. The existing `IEnumerable<IMcpServerConnectionChangedHook>` constructor is retained for unit tests.

- **Container plugin runtime** (`src/Vais.Agents.Runtime.Plugins.Container/`). New `Vais.Agents.Runtime.Plugins.Container` package enables Docker-image-based plugins that speak the IP-1 HTTP protocol (`/v1/invoke`, `/v1/stream`, `/v1/metadata`, `/health`). A container image satisfying the protocol can be declared with `runtime: container` in `plugin.yaml` and the runtime handles the full lifecycle.

  - `ContainerPluginHostService : IHostedService` — scans the configured `PluginsDirectory` for `plugin.yaml` files with `runtime: container`, pulls and starts each container via Docker.DotNet, validates ABI (version range + `handlerTypeName` from `GET /v1/metadata`), and registers a `ContainerAgentShimFactory` per plugin. Non-fatal: a plugin that fails to start or fails ABI validation is logged and skipped; the service continues for the remaining plugins.
  - `ContainerSupervisor` — Docker.DotNet lifecycle manager. Removes any existing container by name, creates and starts a fresh container with the declared image and port mapping, then polls `/health` until ready (configurable timeout). Exposes `DrainAndReplaceAsync` for zero-downtime hot-swap: waits for in-flight invocations to drain, stops the old container, optionally swaps the image tag, and restarts.
  - `ContainerAgentShim : IAiAgent, IStreamingAiAgent, IOpaqueStateCarrier, IAgentGrainStateConsumer` — HTTP-backed agent implementation. Seeds `_history` and `_opaqueStateJson` from the grain's persisted state via `SetGrainState` (called by `AiAgentGrain` after factory creation). On `AskAsync`, runs the preprocessor chain, builds a `PluginInvokeRequest`, and sends `POST /v1/invoke`. On `StreamAsync`, sends `POST /v1/stream` with `Accept: text/event-stream` and yields `CompletionDelta` events from SSE `delta` frames followed by a terminal `TurnCompleted`. Transparent fresh-start retry: on HTTP 422 `OpaqueStateDeserializationError`, clears `_opaqueStateJson` and retries once; a second 422 throws `OpaqueStateDeserializationException`.
  - `ContainerAgentShimFactory : IAgentHandlerFactory` — resolves `IAgentPreprocessor` implementations from DI (sorted by `Order`), creates an `HttpClient` targeting the container's port, and instantiates `ContainerAgentShim`.
  - `HmacCallTokenService : ICallTokenService` — generates and validates short-lived bearer tokens scoped to a `(runId, agentId)` pair. Token format: `base64url(payload).base64url(hmac)` where payload is `{runId}:{agentId}:{expiresAtUnixSeconds}` and HMAC is SHA-256 keyed from `Vais:ContainerPlugin:CallTokenSecret` (≥ 32 characters required).
  - `MapContainerGatewayEndpoints(this IEndpointRouteBuilder)` — maps the internal callback routes (`POST /v1/container-gateway/llm/complete` via `ICompletionProvider` and `POST /v1/container-gateway/tools/invoke` via `IToolCallDispatcher`) behind a call-token validation filter. Must be called on the internal-port pipeline (typically port 5001).
  - `AddContainerPlugins(this IServiceCollection, Action<ContainerPluginLoaderOptions>?)` — DI registration entry point; registers `ICallTokenService`, `ContainerPluginLoaderOptions`, and the hosted service.
  - `ContainerPluginLoaderOptions` — `PluginsDirectory` (default `"plugins"`), `InternalGatewayBaseUrl` (default `"http://localhost:5001"`), `SupportedApiVersionMin`/`Max` (default `"0.24"`).

- **Container plugin preprocessing pipeline** (`src/Vais.Agents.Runtime.Plugins.Container/Preprocessing/`). Two built-in `IAgentPreprocessor` implementations now ensure every container `InvokeRequest.messages` carries the full conversation context — `[System, User₁, Asst₁, …, UserCurrent]` — without any changes to `ContainerAgentShim`.

  - `HistoryAssembler` (Order 0) — prepends the grain's persisted conversation history to the current-turn seed message. No-allocation fast path when history is empty; does not mutate the input list.
  - `SystemPromptInjector` (Order 10) — resolves the system prompt from three sources in priority order: (1) `IAgentGrainStateView.SystemPrompt` (runtime grain override), (2) `AgentManifest.SystemPrompt.Inline` (literal text), (3) `AgentManifest.SystemPrompt.TemplateRef` via `IPromptTemplateRegistry`, (4) `AgentManifest.SystemPrompt.FileRef` via `IPromptFileLoader`. Variable substitution (`{{key}}`) applied after loading. Empty-string grain overrides fall through to the manifest spec. Resolution failures throw `InvalidOperationException` with a `ContainerPluginUrns.SystemPromptResolutionFailed` URN for structured diagnostics.
  - `AddContainerPlugins` updated to register both preprocessors. `IPromptTemplateRegistry` and `IPromptFileLoader` are optional — `SystemPromptInjector` receives `null` for either when not registered in DI.
  - `AddAgentPreprocessor<T>(this IServiceCollection)` — new public extension method for custom preprocessor registration. Custom preprocessors (e.g. memory injection, policy enforcement) register without modifying `ContainerAgentShim`; the convention is `Order >= 100` to run after the two built-ins.

- **`IAgentGrainStateConsumer`** (new interface in `Vais.Agents.Abstractions`). Opt-in post-creation hook for agent implementations that need a live view of the grain's persisted state before the first turn. `AiAgentGrain.OnActivateAsync` checks for the interface after `factory.CreateAsync` and calls `consumer.SetGrainState(_state.State)` if present. Used by `ContainerAgentShim` to seed conversation history and opaque state from the Orleans checkpoint; keeps the grain code unchanged for all other agent types. `AiAgentGrainState` now implements `IAgentGrainStateView` (explicit `IReadOnlyList<ChatTurn>` projection alongside the mutable `List<ChatTurn>` property).

- **`StatefulAiAgent.InvokeAsync(userMessage, ct)`** — convenience alias for `AskAsync`; removes the awkward pattern of calling `AskAsync` when the name reads as a question rather than an action. Both methods delegate to the same core path.

- **`GraphInterrupted.CurrentState`** (`IReadOnlyDictionary<string, JsonElement>?`) — exposes the live graph state bag at the time the interrupt fired. `InProcessGraphOrchestrator` and `MafGraphOrchestrator` both populate it. HITL handler delegates can now read `interrupted.CurrentState` to build approval prompts without independently reloading the checkpoint.

### Fixed

- **`ToolCallOutcome.Result` is now nullable (`string? Result`).** Tool implementations that signal failure via `Error` have no meaningful `Result` to supply; the previous non-nullable signature forced callers to pass `string.Empty` to satisfy the compiler. Null guards added in `ToolResponseTruncationMiddleware`, `ToolOutputLengthGuard`, `ToolHtmlToMarkdownMiddleware`, `ToolJsonRepairMiddleware`, and `StatefulAiAgent`. **Breaking change:** `ToolCallOutcome(callId, result, error)` call sites that pass `string.Empty` for `result` on the error path should change to `null`.

- **`NodeAgentInvoked` stream/bus symmetry in `InProcessGraphOrchestrator`.** The event was being published to `IAgentGraphEventBus` inside `ExecuteNodeAsync` but the streaming loop never yielded it, so `StreamAsync` consumers received one fewer event per agent-kind node than the bus. Fixed by changing `ExecuteNodeAsync` to return a `(Output, NodeAgentInvoked?)` tuple; the main loop yields the event (when non-null) before `NodeCompleted`. `MafGraphOrchestrator` was unaffected — it translates `NodeAgentInvokedEvent` from the MAF infrastructure and was already symmetric.

### Added (continued)

- **31 new runnable samples and 3 doc-only sample directories** covering all post-v0.6 pillars. Samples are deterministic (scripted providers, no API key) unless noted.

  - v0.7 MCP inbound: `McpServerStdio` (`AddMcpAgentServerStdio` + `--demo` mode), `McpServerHttp` (live `McpClient` round-trip).
  - v0.8 A2A inbound: `A2AServerBasics` (card discovery + message round-trip), `A2AInterruptResumeOrleans` (`OrleansTaskStore` + interrupt → resume).
  - v0.9 graph orchestration: `AgentGraphInProcess` (typed state + `PropertyMatcher` routing), `AgentGraphYamlLoader` (YAML manifest), `AgentGraphMaf` (`MafGraphOrchestrator`), `AgentGraphResumeOnOrleans` (`OrleansCheckpointer` halt-mode HITL).
  - v0.10 streaming: `StreamingFilterTypingIndicator` (`IStreamingAgentFilter` around-provider + per-delta hooks), `StreamingResiliencePolly` (Polly retry before first delta).
  - v0.11 HTTP polish: `HttpIdempotencyInMemory` (`AddAgentControlPlaneIdempotency` + replay header), `OpenApiSpecExplorer` (`AddAgentControlPlaneOpenApi` + `x-vais-type-urns` extension).
  - v0.12 HTTP streaming: `HttpStreamingInvoke` (SSE over `MapAgentControlPlane`), `HttpStreamingCancellation` (mid-stream `CancellationTokenSource`).
  - v0.13 Kubernetes operator: `KubernetesOperatorQuickstart` (doc-only — Helm walkthrough + `vais.io/v1alpha1` Agent CR).
  - v0.14 OPA: `OpaPolicyGateLocal` (`AddOpaPolicyEngine` + `LoggerAuditLog`; requires local `opa run --server`). `opa-policies/` extended with `time-window.rego` and `max-concurrent-runs.rego`.
  - v0.15 CLI: `CliCookbook` (doc-only — 4 shell recipes + 3 config starters).
  - v0.40 LLM gateway middleware: `LlmGatewayMiddleware` (`LlmFallbackMiddleware` + `LlmSemanticCacheMiddleware` + `LlmJsonOutputMiddleware<T>`).
  - v0.40 MCP gateway middleware: `McpGatewayMiddleware` (`ToolRetryMiddleware` + `ToolResultCacheMiddleware` + `ToolArgumentValidationMiddleware`).
  - v0.40 OpenAI-compat gateway: `OpenAiCompatGateway` (`AddOpenAiCompatGateway` + `MapOpenAiCompat`; non-streaming and SSE streaming).
  - v0.42 live-mode HITL: `GraphHitlLiveMode` (`IHitlAgentGraph<TState>.StreamWithHitlAsync` inline handler on `MafGraphOrchestrator`).
  - v0.53 PowerFx edge predicates: `GraphPowerFxPredicates` (`PowerFxGraphExpressionEvaluator` + `=Not(IsBlank(Local.research_plan))` YAML edge predicates).
  - `samples/README.md`: count updated (21 → 52 runnable), index table extended, learning path extended to 16 steps, `Build all` block updated (40 projects across 5 categories), "Tooling-only samples" section added.
  - `samples/build-all.ps1` / `samples/build-all.sh`: extended with all new runnable samples; added `$orleans` category (in-process silo, no Docker) and `$opa` category (external OPA server required).

- **CLI and deployment endpoint for container plugins (IP-5).** End-to-end tooling for building, pushing, and hot-reloading Docker-image-based plugins.

  - `IContainerPluginReloader` / `DefaultContainerPluginReloader` — public interface and default implementation for runtime-side hot-reload. `DefaultContainerPluginReloader` looks up the plugin's `ContainerSupervisor` by name and calls `DrainAndReplaceAsync`; returns a structured `ContainerPluginReloadResult` covering `Success`, `NoSupervisor`, `HandlerTypeNameChanged`, `PullFailed`, `StartFailed`, `HandshakeFailed`, and `DrainTimeout`.
  - `ContainerPluginReloadStatus` enum + `ContainerPluginReloadResult` record + `ContainerPluginUrns` additions (`NoSupervisor`, `HandlerTypeNameChanged`) — public contracts in `Vais.Agents.Runtime.Plugins.Container`.
  - `IContainerPluginHost` — public interface exposing `IReadOnlyList<LoadedContainerPlugin> LoadedPlugins`; implemented by `ContainerPluginHostService`.
  - `POST /v1/plugins/{name}/image` — new endpoint in the control plane. Returns 200 on success, 404 when the plugin is not supervised, 422 when the new image declares a different `handlerTypeName` (silo restart required), 503 when `IContainerPluginReloader` is not registered.
  - `GET /v1/plugins` extended to include container plugins with `kind: Container` and `image` fields.
  - `PluginKind.Container = 2` added to `PluginContracts`; `PluginInfo.Image` property added (null for assembly/Python plugins).
  - `PluginImageUpdateRequest` / `PluginImageUpdateResponse` / `PluginImageUpdateStatus` — new HTTP contracts.
  - `AgentControlPlaneClient.PushPluginImageAsync` — typed client method for the new endpoint.
  - `AddContainerPlugins` updated to register `IContainerPluginReloader` as a singleton.
  - `vais plugin-build` — new CLI command. Reads `spec.image` from `plugin.yaml` (overridable via `--image`), runs `docker build -t <image> <context>`, optionally runs `docker push` with `--push` flag.
  - `vais plugin-init` — new CLI command. Scaffolds `plugin.yaml` + `Dockerfile` for a new container plugin; `--runtime python|dotnet` selects the SDK-specific Dockerfile template.
  - `vais plugin push` extended with image-push mode: positional arg containing `/` or `:`, or explicit `--image` flag, triggers `docker push` + `POST /v1/plugins/{name}/image` instead of source tar.gz upload.
  - `vais plugin-status` updated to show `IMAGE` column for container plugins alongside KIND and STATE.

- **Input/output payload capture in all three gateway event stores.** `GatewayEvent`, `McpGatewayEvent`, and `McpEvent` records now carry optional `InputJson` and `OutputJson` fields (up to 32 KB each, truncated with a `[truncated]` suffix). Schema migration uses `ADD COLUMN IF NOT EXISTS` so existing rows are unaffected.

  - `GatewayEventMiddleware` serializes `request.History` as input JSON and accumulates streaming text deltas (or reads `response.Text` for non-streaming) as output JSON.
  - `McpGatewayEventMiddleware` captures `context.Arguments.GetRawText()` as input and `outcome.Result ?? outcome.Error` as output.
  - `McpEventMiddleware` applies the same capture pattern as `McpGatewayEventMiddleware`.
  - All three Postgres stores extend their `INSERT` and `SELECT` statements for the new columns.
  - `GatewayEventDto`, `McpEventDto`, `McpGatewayEventDto` in `GraphContracts.cs` expose `InputJson` and `OutputJson`; HTTP responses include both fields.
  - Workbench gateway/MCP event tabs render expandable **Input** and **Output** payload blocks (pretty-printed JSON, `max-height: 300px`).

### Fixed

- **`run_id` null in all gateway event stores.** `OrleansAgentContextAccessor.Current` now reads `ActivityPropagation.ReadGraphRunId()` from Orleans `RequestContext` and exposes it as `AgentContext.RunId`. `GatewayEventMiddleware`, `McpGatewayEventMiddleware`, and `McpEventMiddleware` now write `RunId: _ctx.Current.RunId` instead of `null`.

- **Streaming token counts always zero.** `MafCompletionProvider.StreamAsync` now sets `chatOptions.AdditionalProperties["stream_options"] = new { include_usage = true }`, instructing the OpenAI MEAI bridge to include usage data on the final streaming chunk.

- **`correlation_id` never propagated across grain boundaries.** `OrleansOutgoingActivityFilter` now writes `CorrelationId` to Orleans `RequestContext` (alongside the existing RCB fields) on each outgoing grain call. HTTP ingress handlers (`InvokeAsync`, `InvokeStreamAsync`, `GraphInvokeAsync`, `GraphInvokeStreamAsync`) now resolve the `X-Correlation-Id` header (or auto-generate a GUID) and inject it on the `AgentContext` principal; `AiAgentGrain.StreamAgentAsync` bridges the context parameter's `CorrelationId` into `RequestContext` at grain entry.

- **Plugin .NET agents emit no LLM gateway events.** `ResearchPlannerAgent` now accepts optional DI-injected `ICompletionProvider` and `IEnumerable<LlmGatewayMiddleware>` via its constructor. When the provider is present, LLM calls route through `LlmGatewayPipeline.InvokeAsync` so `GatewayEventMiddleware` records them; the raw-HTTP path is retained as a fallback when no provider is available.

- **Plugin .NET agents emit no tool gateway events.** `AgentManifestTranslator` now resolves the DI-fallback `ToolGatewayMiddleware` array and sets it on the plugin branch of `StatefulAgentOptions`. `AiAgentGrain` stores the middleware array at activation and, after each `AskAsync` turn in plugin mode, scans the history delta for `ToolCallRequest` entries and invokes the middleware chain (with a no-op inner delegate) to record each tool call to `McpGatewayEventStore`. Duration is recorded as 0 ms (calls are not individually timed).

- **Gateway event tabs empty when agent uses a named gateway ref.** `GatewayEventMiddleware`, `McpEventMiddleware`, and `McpGatewayEventMiddleware` were registered only as DI singletons, which `AgentManifestTranslator` bypasses when building a named-gateway pipeline. Each event-store extension now also registers a `NamedLlmGatewayMiddlewareRegistration` / `NamedToolGatewayMiddlewareRegistration` (`"LlmGatewayLogging"`, `"McpGatewayLogging"`, `"McpServerLogging"`). The research-pipeline gateway YAML manifests (`llm-gateway.yaml`, `mcp-gateway.yaml`) include those names so events are captured regardless of whether the agent manifest carries a gateway ref.

- **MCP server shows no outbound references in Workbench.** `RefsTab.tsx` `buildOutboundRows()` had no `mcp-servers` case, so the `mcpGatewayRef` field on `mcp-tavily` was invisible. Added the case to display `mcpGatewayRef` as a clickable outbound link. `McpServerManifest` in `types.ts` now includes `mcpGatewayRef?: string`.

- **Gateway middleware dropped for declarative agents.** `AiAgentGrain.OnActivateAsync` was rebuilding `StatefulAgentOptions` from the translator-supplied options but omitting `GatewayMiddleware` and `ToolGatewayMiddleware`. The `StatefulAiAgent` therefore ran with an empty filter chain, so `GatewayEventMiddleware` / `McpEventMiddleware` / `McpGatewayEventMiddleware` were never invoked and no events were written to Postgres. Fixed by forwarding both fields in the `seeded` options.

- **`/readyz` threw `InvalidOperationException` in containerised Kestrel.** `Utf8JsonWriter.Dispose()` unconditionally calls `Flush()` synchronously, which Kestrel's Alpine response stream disallows. Changed `using var writer` to `await using var writer` in `WriteReadyzJsonAsync` so `DisposeAsync()` is used instead. Affected runtime pods running in Kubernetes (Docker topology was unaffected because the host Kestrel allowed sync IO in that environment).

- **Kubernetes container plugin fails ABI check when plugin pod isn't ready at runtime startup.** `ContainerPluginHostService.StartOneAsync` previously called `GET /v1/metadata` once with a 10 s timeout. For Kubernetes topology the plugin deployment may not exist yet (runtime starts first). Added a retry loop for K8s descriptors: retries every 2 s for up to `StartupTimeoutSeconds` (default 30 s), falling through to a final hard failure only when the deadline expires.

- **`IPluginHandlerRegistry` not registered when `VAIS_PLUGINS_DIRECTORY` is unset.** When the assembly-plugin loader is disabled (empty directory env var), `AddAgentPlugins` is never called, leaving `IPluginHandlerRegistry` absent from the DI container and causing `ContainerPluginHostService` to fail at resolution. Added `EnsurePluginRegistry(this IServiceCollection)` — an idempotent `TryAddSingleton` wrapper — and called it as the first statement in `AddContainerPlugins`.

### Added (continued)

- **Kubernetes standalone topology for container plugins (IP-6).** Container plugins can now run as Kubernetes Deployments instead of local Docker containers. The runtime monitors the existing K8s Deployment and delegates image rollouts via the Kubernetes API — it does not own the pod lifecycle.

  - `IContainerSupervisor` — new interface extracted from `ContainerSupervisor` (now `DockerContainerSupervisor`). Declares `StartAsync`, `StopAsync`, `DrainAndReplaceAsync`, `WaitForHealthAsync`, `TryAcquireInvoke`, `ReleaseInvoke`, and `DisposeAsync`. Enables a clean seam for plugging in alternative topology implementations.
  - `DockerContainerSupervisor` — renamed/refactored from the previous monolithic `ContainerSupervisor`. Manages Docker container lifecycle unchanged.
  - `KubernetesContainerSupervisor` — new supervisor for K8s topology. At startup it waits up to `StartupTimeoutSeconds` for the plugin's ClusterIP Service `/health` to be reachable; on timeout it logs a warning and continues (K8s manages pod restart). `DrainAndReplaceAsync` issues a `PATCH apps/v1/namespaces/{ns}/deployments/{name}` merge-patch to update the container image and returns `RolloutStarted` immediately.
  - `KubernetesPluginConfig` — record carrying `ServiceUrl`, `DeploymentName`, and `Namespace` parsed from the `kubernetes:` block in `plugin.yaml`.
  - `ContainerPluginYamlDeserializer` — extended to deserialise the `kubernetes:` block (`serviceUrl`, `deploymentName`, `namespace`). When present, `ContainerPluginHostService` builds a `KubernetesContainerSupervisor` backed by an in-cluster `IKubernetes` client.
  - `GET /v1/plugins` — response includes `topology` (`standalone` or `kubernetes`), `kubernetesDeploymentName`, and `kubernetesNamespace` fields for container plugins.
  - `LoadedContainerPlugin` / `IContainerPluginHost` — `Topology`, `KubernetesDeploymentName`, and `KubernetesNamespace` properties added.
  - `PluginInfo` wire type — `Topology`, `KubernetesDeploymentName`, `KubernetesNamespace` fields added; `vais plugin-status` renders them in the table and JSON output.
  - Runtime Helm chart (`deploy/helm/vais-agents-runtime/`) — new `rbac.yaml` template adds a `ClusterRole` + `ClusterRoleBinding` giving the runtime ServiceAccount `get/list/watch/patch` on `apps/deployments` and `core/pods` when `rbac.pluginSupervision: true`. Controlled by the new `rbac.create` and `rbac.pluginSupervision` values.

- **`vais plugin-deploy` CLI command.** Deploys a container plugin to Kubernetes using the built-in `vais-plugin` embedded Helm chart and registers it with the running control plane in one step (P11 — no runtime restart required).

  - `PluginDeployCommand` — `vais plugin-deploy <release-name> --image <image> [--namespace <ns>] [--replicas N] [--port N] [--image-pull-policy <policy>] [-f values.yaml] [--set k=v] [--dry-run] [--no-apply] [--context <ctx>] [--token <tok>]`. Extracts the embedded chart to a temp directory, runs `helm upgrade --install`, then — on success and unless `--no-apply` is set — builds a `ContainerPluginManifest` from CLI args and calls `CreateContainerPluginAsync` (falls back to `UpdateContainerPluginAsync` on 409 Conflict).
  - `--image-pull-policy <policy>` (default `IfNotPresent`) — forwarded to Helm as `--set image.pullPolicy=<value>`; eliminates the need for a manual `kubectl patch imagePullPolicy` step (relevant for Docker Desktop where `Never` is required for local images).
  - `--no-apply` — skip control plane registration; useful for bootstrap flows where the runtime is not yet running.
  - `EmbeddedChartExtractor` — extracts `src/Vais.Agents.Cli/Charts/vais-plugin/` (embedded resource) to a temp folder on each invocation; caller is responsible for cleanup.
  - Embedded `vais-plugin` Helm chart (`src/Vais.Agents.Cli/Charts/vais-plugin/`) — minimal Deployment + ClusterIP Service chart with `pluginName`, `image.*`, `replicaCount`, `pluginPort`, `env`, `resources`, and `imagePullSecrets` values; readiness/liveness probes target `GET /health`.
  - `HelmRunner` / `KubectlRunner` — thin process wrappers that stream stdout/stderr and return the exit code; mockable via `static Func` for unit tests.

- **`kind: ContainerPlugin` is now a first-class apply-able resource** (P11 — closes the container plugin gap). `vais apply -f plugin.yaml` now works for `ContainerPlugin` manifests exactly as it does for `Agent`, `AgentGraph`, `McpServer`, and the other resource kinds.

  - `ContainerPluginManifest` — new sealed record (`metadata.id`, `metadata.version`, `metadata.description`, `metadata.labels`, `spec`) registered with YAML and JSON loaders. `ContainerPluginSpec` fields: `image`, `port` (default 8080), `topology` (`standalone` | `kubernetes`), `build` (see build-on-apply entry below), `startupTimeoutSeconds`, `invokeTimeoutSeconds`, `kubernetes` (`serviceUrl`, `deploymentName`, `namespace`), `secrets`, `imagePullPolicy`. `ManifestResource.ContainerPluginCase` DU case added; all manifest loaders emit it for `kind: ContainerPlugin`.
  - `ContainerPluginRegistryGrain` — new Orleans grain that persists registered plugin manifests across silo restarts. Implements `IContainerPluginRegistry` (grain interface) with `RegisterAsync`, `GetAsync`, `ListAsync`, and `EvictAsync`. State stored via the standard Orleans `IPersistentState<ContainerPluginRegistryState>` checkpoint.
  - `IContainerPluginHost.RegisterAsync(ContainerPluginManifest)` — new method on the host service. When a plugin with the same ID is already supervised, `RegisterAsync` calls `DrainAndReplaceAsync` on the existing supervisor to hot-swap to the new image; otherwise it creates a fresh supervisor and starts the container. On first registration the manifest is also written to `ContainerPluginRegistryGrain`.
  - HTTP endpoints — new routes under `/v1/container-plugins`:
    - `POST /v1/container-plugins` — registers a new plugin; returns 409 if the ID already exists.
    - `PATCH /v1/container-plugins/{id}` — updates an existing plugin manifest and triggers hot-swap.
    - `GET /v1/container-plugins` — lists all registered manifests with optional `labelPrefix` and `limit` query params.
    - `GET /v1/container-plugins/{id}` — returns the manifest for a single plugin.
    - `DELETE /v1/container-plugins/{id}` — evicts the plugin (stops the container and removes the registry entry).
  - `IAgentControlPlaneClient` — new default-interface methods `CreateContainerPluginAsync`, `UpdateContainerPluginAsync`, `ListContainerPluginsAsync`, `QueryContainerPluginAsync`, `EvictContainerPluginAsync`, `ValidateContainerPluginAsync`. `AgentControlPlaneClient` implements all six against the new endpoints. Default implementations on the interface throw `NotSupportedException` so existing partial fakes compile without change.
  - `ApplyCommand` — `ContainerPluginCase` branch added; calls `ApplyContainerPluginAsync` (create-or-update via 409 catch) and propagates the bool return so a build/push failure sets the exit code.
  - Filesystem-to-registry promotion (CP-6) — `ContainerPluginHostService.StartAsync` now writes each filesystem-discovered plugin manifest into `ContainerPluginRegistryGrain` on first sight, bridging the legacy `plugin.yaml` drop-in path with the durable registry. Subsequent restarts load from the grain, not the filesystem.

- **Build-on-apply for `ContainerPlugin` manifests.** When `spec.build` is present in a `ContainerPlugin` manifest, `vais apply -f plugin.yaml` automatically runs `docker build` (and optionally `docker push`) before posting the manifest to the control plane.

  - `ContainerPluginBuildSpec` — `context` (relative path to build context, default `./`), `dockerfile` (relative to context, default `Dockerfile`), `args` (map of `--build-arg` key/value pairs), `push` (bool; if true, `docker push` runs after a successful build).
  - **Cache-aware build** — `DockerRunner.ImageExistsAsync` runs `docker image inspect <image>` before building; if the image tag is already present locally the build step is skipped. Bypassed when `--no-build` is set.
  - `--no-build` flag on `vais apply` — skips the build step entirely; useful for CI pipelines that build and push as a separate stage (`vais plugin-build --push` then `vais apply -f ... --no-build`).
  - `DockerRun` and `DockerImageExists` injectable `internal static Func` hooks on `ApplyCommand` — allow unit tests to intercept Docker calls without a live Docker daemon.
  - `ApplyContainerPluginAsync` is `internal static` — directly testable from `Vais.Agents.Cli.Tests` via the existing `InternalsVisibleTo` assembly attribute.
  - `samples/quickstart-python-planner/` — new sample demonstrating one-command build-and-deploy. `plugin.yaml` carries a `spec.build` block; `vais apply -f samples/quickstart-python-planner/plugin.yaml` builds the image, registers the plugin, and the runtime starts the container in one step. Includes `Dockerfile`, `server.py` (stdlib HTTP server implementing IP-1), and `pyproject.toml` (`openai` dependency).
  - QUICKSTART step 6 rewritten — removes the manual `mkdir` + filesystem copy workflow; replaces it with a single `vais apply -f samples/quickstart-python-planner/plugin.yaml` command.

- **E2E test suites** (`tests/e2e/`). Two end-to-end suites exercise the container plugin stack against real Docker and Kubernetes runtimes.

  - `tests/e2e/shared/echo-plugin/` — minimal Python HTTP server implementing the full IP-1 contract (`/health`, `/v1/metadata`, `/v1/invoke`, `/v1/stream`). Returns `handlerTypeName: echo.EchoPlugin`, `targetApiVersion: 0.24`, and echoes the last user message.
  - `tests/e2e/docker/run.ps1` — Docker topology suite (11 assertions). Publishes the runtime host, starts it with no filesystem plugin directory (`VAIS_CONTAINER_PLUGINS_DIRECTORY=""`), waits for `/healthz`, registers the plugin via `vais apply -f plugin.yaml --no-build` (CLI-publish path, P11), asserts plugin reaches `Ready` state, hot-reloads the image via `POST /v1/plugins/{name}/image`, and verifies `plugin-status --output json`. `tests/e2e/docker/plugin.yaml` updated to `vais.agents/v1` schema (`kind: ContainerPlugin`, `metadata.id`).
  - `tests/e2e/kubernetes/run.ps1` — Kubernetes topology suite (8 assertions). Installs the runtime via Helm, port-forwards the control-plane service, writes a `vais` config, runs `vais plugin-deploy echo-plugin --image vais-echo:test --namespace <ns> --image-pull-policy Never --replicas 1` (one command: Helm deploy + control plane register), waits for plugin `Ready`, and asserts topology fields and `RolloutStarted` response from the reload endpoint. No longer requires a separate `kubectl patch` step.
  - `tests/e2e/kubernetes/Dockerfile.e2e` — multi-stage build that compiles the runtime; does not bake in any `plugin.yaml` descriptor (plugins registered at runtime via CLI).
  - `tests/e2e/README.md` — prerequisites, step-by-step run instructions, and pass criteria for both suites.
  - `ApplyCommandBuildOnApplyTests` (10 tests in `Vais.Agents.Cli.Tests`) — unit tests for the build-on-apply path using injectable static hooks: image-not-cached triggers build, cached image skips build, `--no-build` skips regardless, push flag runs two Docker calls, build failure returns false without posting manifest, 409 falls back to update, `--build-arg` values are forwarded, stdin manifest (`-`) resolves context to CWD.

---

## [0.55.0-preview] — 2026-05-05

### Added

- **MCP gateway tool-call event log** (`src/Vais.Agents.Observability.McpGatewayEventStore/`). New `Vais.Agents.Observability.McpGatewayEventStore` package mirrors `McpEventStore` at the gateway level — every tool dispatch that passes through the MCP gateway middleware stack is persisted to Postgres and exposed via `GET /v1/mcp-gateways/{id}/events`.

  - `IMcpGatewayEventStore` / `McpGatewayEvent` / `PostgresMcpGatewayEventStore` — same structure as `McpEventStore` but keyed by `GatewayId`; table `vais_mcp_gateway_events`.
  - `McpGatewayEventMiddleware : ToolGatewayMiddleware` — fire-and-forget recording after every tool call; records `call.completed` or `call.failed` with wall-clock duration, error type, and ambient correlation/run IDs.
  - `AddMcpGatewayEventStore(Action<McpGatewayEventStoreOptions>)` DI extension; `McpGatewayEventStoreInitializer` applies schema and prunes old rows on startup.
  - `RuntimeOptions` — new `VAIS_MCP_GATEWAY_EVENT_STORE_CONNECTION` + `VAIS_MCP_GATEWAY_ID` env vars; `local-dev/docker-compose.dev.yml` pre-wired to `research-mcp-gateway`.
  - HTTP: `GET /v1/mcp-gateways/{id}/events` added to `MapMcpGatewayControlPlane`; query params `since`, `until`, `toolName`, `kind`, `limit` (max 500). Returns 503 when store not configured.
  - `McpGatewayEventDto` added to `GraphContracts.cs`.
  - Workbench: `McpGatewayEventDto` in `types.ts`, `listMcpGatewayEvents` in `resources.ts`, new `McpGatewayEventsTab.tsx` (10 s auto-refresh, expandable rows), "Tool Logs" tab on MCP gateway detail pane.

- **Agent stdout log sink** (`src/Vais.Agents.Observability.AgentLogs/`, `src/Vais.Agents.Abstractions/`). New `Vais.Agents.Observability.AgentLogs` package captures per-agent log lines from both .NET grain loggers and Python subprocess stderr into an in-memory ring buffer, exposed via `GET /v1/agents/{id}/logs`.

  - `IAgentLogSink` / `AgentLogEntry` abstractions added to `Vais.Agents.Abstractions`.
  - `InMemoryAgentLogSink` — `ConcurrentDictionary` of per-agent ring buffers; oldest entries evicted when cap is reached. Buffer size configurable via `VAIS_AGENT_LOG_BUFFER_LINES` (default 500).
  - `AgentGrainLoggerProvider : ILoggerProvider, ISupportExternalScope` — intercepts Orleans grain log scopes to extract `AgentId` and forward lines into `IAgentLogSink` tagged `source: grain`.
  - `AddAgentLogSink()` DI extension registers both the sink and the logger provider.
  - `PythonSubprocessSupervisor` — `ForwardStderrAsync` now forwards each line to `IAgentLogSink` tagged `source: python`.
  - `PythonPluginHostService` / `PythonPluginServiceCollectionExtensions` — resolve `IAgentLogSink` from DI and pass to supervisor.
  - HTTP: `GET /v1/agents/{id}/logs?since=&limit=` added to agent control-plane endpoints. Returns 503 when sink not registered.
  - `AgentLogEntryDto` added to `GraphContracts.cs`.
  - Workbench: `AgentLogEntryDto` in `types.ts`, `listAgentLogs` in `resources.ts`, new `AgentLogsTab.tsx` (5 s auto-refresh, level colour coding), "Logs" tab on agent detail pane.

---

## [0.54.0-preview] — 2026-05-05

### Added

- **Workbench — interactive graph topology diagram** (`workbench/`). A new **Graph** tab appears in the DetailPane for graph resources, rendering the pre-run manifest topology as an interactive DAG (pan, zoom).

  - Nodes are laid out automatically via Dagre (`rankdir: TB`). Node kind is indicated by border colour: Agent = teal, End = green, Interrupt = amber, Code = neutral. The entry node has an accent left-border stripe.
  - Concurrent (fan-out) edges are rendered as dashed teal lines; sequential edges as solid dim lines — matching the `graph.fanout` / `graph.node` OTEL span topology.
  - Tab order for graphs: YAML · **Graph** · References · Test · Logs.
  - Components: `graphLayout.ts` (pure Dagre helper, 6 unit tests), `GraphNode.tsx` (custom node renderer), `GraphTab.tsx` (React Query fetch + ReactFlow canvas), `graphTab.css` (design-token overrides for React Flow chrome).
  - Dependencies added: `@xyflow/react`, `@dagrejs/dagre`.
  - Architecture note: the `GraphTab` is read-only today (`nodesDraggable/nodesConnectable/elementsSelectable` all false). A future live-execution overlay can be added via an optional `runId` prop that highlights active nodes — no layout changes needed.

- **Langfuse tracing — `graph.fanout` span for fan-out topology visibility** (`src/Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework/`). Concurrent branch nodes now appear nested under a `graph.fanout` parent span in the Langfuse trace waterfall, making the fan-out/fan-in pipeline shape visible without post-processing.

  - Fork-source executors open a `graph.fanout` activity (tagged with `graph.node.id` and `graph.run_id`), capture its `ActivityContext`, then close it immediately. The context is forwarded to branch nodes via the new `internal ActivityContext? FanoutContext` field on `GraphMessage`.
  - Branch `graph.node` spans use `FanoutContext` as their OTEL parent instead of `graph.run`, producing correct hierarchy in Langfuse.
  - `GraphJoinNodeExecutor` clears `FanoutContext` before delegating to the base body, so the join node (e.g. synthesizer) remains a child of `graph.run` rather than of `graph.fanout`.
  - Resulting Langfuse trace shape for a `planner → [researcher | analyst] → synthesizer → end` graph:

    ```
    graph.run
    ├── planner       (graph.node)
    ├── graph.fanout
    │   ├── researcher (graph.node)
    │   └── analyst    (graph.node)
    └── synthesizer   (graph.node)
    ```

---

## [0.53.0-preview] — 2026-05-05

### Added

- **PowerFx inline edge predicates** (`src/Vais.Agents.Core.PowerFx/`, `src/Vais.Agents.Core/`, `src/Vais.Agents.Abstractions/`). Graph manifest authors can now write boolean PowerFx expressions directly on `when:` edge slots instead of registering a C# `IGraphEdgePredicate` class for every simple condition.

  ```yaml
  edges:
    - from: planner
      to: analyst
      when: "=Not(IsBlank(Local.research_plan))"
    - from: planner
      to: end
      when: "=IsBlank(Local.research_plan)"
  ```

  - New `GraphEdgePredicate.Expression(string Expr)` sealed record in `Vais.Agents.Abstractions` — JSON/YAML manifest loaders map any `when: "=..."` string to this type.
  - New `IGraphExpressionEvaluator` interface in `Vais.Agents.Core` — pluggable evaluator used by `GraphPredicateEvaluator.EvaluateAsync`. Null means `Expression` predicates throw with a message directing to `AddPowerFxExpressionEvaluator()`.
  - New `Vais.Agents.Core.PowerFx` package — `PowerFxGraphExpressionEvaluator` backed by `Microsoft.PowerFx.Interpreter` 1.8.1. State keys exposed under `Local.*` (hyphens normalised to underscores); `Local.lastMessage` shortcut for the last `messages` array entry.
  - `AddPowerFxExpressionEvaluator()` DI extension registers the evaluator as the singleton `IGraphExpressionEvaluator`.
  - `expressionEvaluator` parameter threaded through `InProcessGraphOrchestrator`, `MafGraphOrchestrator`, `MafGraphBuilder`, and `GraphNodeExecutor`.
  - `Vais.Agents.Runtime.Host` wired: `AddPowerFxExpressionEvaluator()` called in `CompositionRoot` so any graph deployed to the runtime can use inline expressions without extra configuration.
  - Docs: `docs/reference/graph-predicate-operators.md` updated — new `Expression` subtype row, inline expression guide, updated .NET API shape.
  - Docs: `docs/reference/packages.md` — `Vais.Agents.Core.PowerFx` row added; package count updated to 40.

---

## [0.52.0-preview] — 2026-05-03

### Added

- **LangGraph researcher — real web search via Tavily MCP** (`samples/PluginAgentLangGraphResearcherLive/`). Graph extended from two nodes to three: `plan → search → summarize`. The `_node_search` async node calls the Tavily MCP server via `langchain-mcp-adapters` for each generated research question and passes the results to the summarizer. Requires `TAVILY_MCP_URL` secret (defaults to `http://tavily-mcp:8000/mcp`).
  - New dependency: `langchain-mcp-adapters >=0.1,<1`
  - New secret binding: `TAVILY_MCP_URL: "secret://env/TAVILY_MCP_URL"` in `plugin.yaml`; invoke timeout raised to 120 s.
  - New MCP manifest: `samples/PluginAgentResearchPipeline/gateways/mcp-server-tavily.yaml`
  - Tavily MCP server source: `samples/PluginAgentLangGraphResearcherLive/tavily-mcp-server/`

### Fixed

- **SGR analyst — `api_key` never reached `WebSearchTool` at invocation time** (`samples/PluginAgentResearchPipeline/sgr-analyst/`). Root cause: `sgr-agent-core` 0.7.0's `NextStepToolsBuilder` creates `D_<ToolName>` subclasses dynamically; `BaseTool.__init_subclass__` then sets `D_WebSearchTool.tool_name = "d_websearchtool"`, shadowing the parent's `"websearchtool"`. The `_action_phase` lookup `tool_configs.get(tool.tool_name, {})` returned `{}` → `WebSearchConfig(api_key=None)` → `ValueError`. Fixed by aliasing `d_{key}` entries into `tool_configs` after `AgentFactory.create()`. Added diagnostic logging to `research.py` for future debugging.
- **Research pipeline analyst node received wrong input** (`samples/PluginAgentResearchPipeline/research-pipeline.yaml`). `stateBindings.input` was `[research_findings]` — the researcher's output — so the analyst rewrote existing findings instead of running independent research. Corrected to `[query, research_plan]`.
- **`InProcessGraphOrchestrator.BuildAgentInputText` ignored binding filter** (`src/Vais.Agents.Core/`). Graph state always carries `messages` from prior nodes; nodes that did not declare `messages` as an input key still received it as their primary text, shadowing their declared key (e.g. `query`). Fixed by calling `FilterByInputBinding` before `BuildAgentInputText` and `BuildMetadata`.
- **`sgr-analyst` missing `TAVILY_API_KEY` secret declaration** (`samples/PluginAgentResearchPipeline/sgr-analyst/plugin.yaml`). Secret was present in the runtime env but absent from the plugin manifest, so the subprocess never received it.

---

## [0.51.0-preview] — 2026-05-03

### Added

- **Vais Workbench — dark theme design system** (`workbench/`). Full visual redesign of the desktop app; all Tailwind utility-class strings replaced with a BEM CSS architecture keyed to semantic design tokens.
  - **Design tokens** — 16 CSS custom properties in `src/index.css` (registered in both `:root` and Tailwind v4 `@theme`). Charcoal dark palette; teal/cyan accent (`#2dd4bf`); VS Code Dark+ Monaco theme (`src/monacoTheme.ts`) applied to both editor instances.
  - **Shared chrome** (`src/chrome.css`) — `.app` CSS grid shell, header, sidebar sections with per-kind icons and shimmer skeleton rows, tab bar, toolbar, kind badge, modal overlay, button variants (`--primary`, `--ghost`, `--bare`, `--danger`).
  - **Component-local styles** — `src/styles/testPanel.css` (run cards, animated teal streaming cursor), `src/styles/deleteDialog.css` (amber reverse-reference warning callout).
  - **Sidebar footer** — active connection URL and green connected indicator.
  - **Reverse-reference warning in DeleteDialog** — amber callout listing resources that reference the one being deleted (completes WB-7).

---

## [0.50.0-preview] — 2026-05-03

### Added

- **Prometheus metrics endpoint** (`src/Vais.Agents.Runtime.Host/`) — when OTel is enabled (`OTEL_EXPORTER_OTLP_ENDPOINT` or `VAIS_OTEL_CONSOLE`), the runtime now exposes `GET /metrics` in Prometheus text format. Scrapes the `gen_ai.client.token.usage` and `gen_ai.client.operation.duration` instruments from `Vais.Agents.Observability.OpenTelemetry`. Uses `OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.15.0-beta.1 (no stable release exists upstream yet). Wired via `m.AddPrometheusExporter()` in `CompositionRoot.ConfigureObservability` and `app.MapPrometheusScrapingEndpoint()` in `Program.cs`.

- **Vais Workbench** (`workbench/`) — Electron 33 + React 19 + TypeScript 5 desktop app for managing Vais.Agents resources on a running runtime. Connects to the HTTP control plane; no auth required for v1.
  - **Resource tree** — collapsible sidebar listing all five resource kinds (`agents`, `graphs`, `llm-gateways`, `mcp-gateways`, `mcp-servers`) with 5-second polling via TanStack Query v5.
  - **YAML tab** — read-only Monaco editor showing the selected resource manifest. Edit button pre-fills the Deploy pane; Delete button opens a confirmation dialog.
  - **References tab** — outbound (`llmGatewayRef`, `mcpGatewayRef`, `mcpServers[]`) and inbound (agents/graphs that reference the current resource) cross-reference navigation. Clickable links navigate to the referenced resource in the tree.
  - **Deploy pane** — modal Monaco editor with `kind:` auto-detection, `Validate` (→ `POST /v1/<kind>/validate`), and `Apply` (→ `POST /v1/<kind>`). Multi-document YAML applies in dependency order: `llm-gateways → mcp-gateways → mcp-servers → agents → graphs`.
  - **Delete dialog** — confirmation dialog with `DELETE /v1/<kind>/{id}` on confirm; clears selection and invalidates the TanStack Query cache.
  - **Test tab for agents/graphs** — message textarea → `POST /v1/<kind>/{id}/invoke` → streamed response via `ReadableStream`; last 5 runs in component state.
  - **Stub test panels** for `llm-gateways`, `mcp-gateways`, `mcp-servers` (probe endpoints not yet available).
  - **Plugin extension point** — CommonJS `.js` files in `~/.vais/workbench/plugins/` add custom tabs to any resource kind's detail pane. Each plugin exports `{ kind, tabLabel, render(resource) → string }`.
  - **Connection config** — `~/.vais/workbench/config.yaml` stores named connections; active connection saved across restarts.
- Docs: `workbench/docs/quickstart.md` — prerequisites, dev setup, feature walkthrough, config file schema, keyboard shortcuts.
- Docs: `workbench/docs/plugins.md` — plugin contract, two examples, security note.
- **CORS middleware** (`src/Vais.Agents.Runtime.Host/`) — runtime now configures CORS when running in `localhost` mode or when `VAIS_CORS_ORIGINS` is set. In localhost mode, all `localhost`/`127.0.0.1` origins are allowed (enables the Workbench dev server to connect without a proxy). Set `VAIS_CORS_ORIGINS=disabled` to opt out, or supply a comma-separated origin list for production deployments.

### Fixed

- **`AgentManifestTranslator` — `IUsageSink` was never wired** (`src/Vais.Agents.Runtime.Instantiation/`). All declarative agents silently fell back to `NullUsageSink`, causing zero OTel token-usage metrics to be emitted. Fixed by resolving `IUsageSink` from DI when building `StatefulAgentOptions`.
- **Workbench default connection port** (`workbench/src/config/types.ts`). `DEFAULT_CONFIG` pointed to port 5000; corrected to 8080 (the runtime's actual default port).
- **`Vais.Agents.Control.Http.Client` RS0027 build warning** — added `<NoWarn>RS0027</NoWarn>` to suppress the public-API analyser warning on intentional optional-parameter convenience overloads (`CreateLlmGatewayConfigAsync`, `CreateMcpServerAsync`, etc.) that are part of the package's public API surface.
- **`sgr-analyst` Tavily API key not passed to `WebSearchTool`** (`samples/PluginAgentResearchPipeline/sgr-analyst/`). `_make_toolkit()` wrapped `ExtractPageContentTool` in a `ToolDefinition` with the key but left `WebSearchTool` as a bare class reference. Fixed by wrapping both tools in `ToolDefinition(... tavily_api_key=tavily_key)`.
- **Workbench list responses not unwrapped** (`workbench/src/api/`). All five list fetchers treated the API response as a plain array, but the control plane returns `{ "items": [...], "nextCursor": null }`. This caused a runtime `data.map is not a function` crash on startup. Fixed by introducing `ListResponse<T>` and unwrapping `.items` in `listAgents`, `listGraphs`, `listLlmGateways`, `listMcpGateways`, and `listMcpServers`.
- **Workbench `useClient` fallback port** (`workbench/src/api/useClient.ts`). The hardcoded fallback URL used port 5000 while the runtime listens on 8080. Fixed to match `DEFAULT_CONFIG`.
- **Workbench agent invoke field name** (`workbench/src/components/TestPane/AgentTestPanel.tsx`). The test panel sent `{ message }` but `AgentInvocationRequest` requires `{ text }`. Corrected field name.

---

## [0.40.0-preview] — 2026-04-27

### Added
- **Gateway Config Control Plane pillar.** Declarative YAML manifests for `LlmGatewayConfig` and `McpGatewayConfig` — named, versioned middleware pipelines applied at agent activation without redeployment. Any agent can bind to a shared gateway config via `llmGatewayRef` / `mcpGatewayRef`; updating the config (re-`apply`) takes effect on the next grain activation.
  - **Spec types and manifest models** — `LlmGatewayConfigManifest`, `McpGatewayConfigManifest`, `McpServerManifest`, `GatewayMiddlewareSpec` (`name` + `params` dictionary). All types implement `IVersionedManifest`; supported by the existing JSON + YAML manifest loaders.
  - **Registry and lifecycle interfaces** — `ILlmGatewayConfigRegistry`, `IMcpGatewayConfigRegistry`, `IMcpServerRegistry` (CRUD + get-or-null); `ILlmGatewayConfigLifecycleManager`, `IMcpGatewayConfigLifecycleManager`, `IMcpServerLifecycleManager` wiring apply/delete through audit + registry.
  - **In-process implementations** — `InProcessLlmGatewayConfigLifecycleManager`, `InProcessMcpGatewayConfigLifecycleManager`, `InProcessMcpServerLifecycleManager`; `InMemory*Registry` implementations used in tests; Orleans-backed registries (`OrleansLlmGatewayConfigRegistry`, `OrleansMcpGatewayConfigRegistry`, `OrleansMcpServerRegistry`) backed by per-id persistent grains.
  - **HTTP endpoints** — `POST/GET/DELETE /v1/llm-gateways/{id}`, `POST/GET/DELETE /v1/mcp-gateways/{id}`, `POST/GET/DELETE /v1/mcp-servers/{id}` (full CRUD for all three kinds). Mounted by `MapGatewayConfigEndpoints()`.
  - **Eager ref validation** — `POST /v1/agents` now validates `llmGatewayRef` and `mcpGatewayRef` against the live registries; unknown refs return `422 urn:vais-agents:llm-gateway-ref-not-found` / `mcp-gateway-ref-not-found` before the manifest is stored.
  - **CLI commands** — `vais apply -f <manifest>`, `vais get llm-gateways`, `vais get mcp-gateways`, `vais get mcp-servers`, `vais delete llm-gateways/{id}`, `vais delete mcp-gateways/{id}`, `vais delete mcp-servers/{id}`.
  - **Named middleware factory layer** — every gateway package now ships a `services.AddNamed*GatewayMiddleware_<Name>()` DI extension that registers a `Named*GatewayMiddlewareRegistration` singleton. 22 registrations total across `Core` (4 LLM + 4 Tool) and the five `Mcp*` packages (4 LLM + 10 Tool).
  - **`ILlmGatewayMiddlewareFactory` / `IToolGatewayMiddlewareFactory`** — factory interfaces in `Vais.Agents.Abstractions`. `Create(GatewayMiddlewareSpec)` instantiates a named middleware by `spec.Name`, forwarding `spec.Params` as configuration.
  - **`DefaultLlmGatewayMiddlewareFactory` / `DefaultToolGatewayMiddlewareFactory`** — composite factory implementations in `Vais.Agents.Core` that collect all registered `Named*GatewayMiddlewareRegistration` singletons from DI and dispatch `Create` by case-insensitive name. On unknown name, throws `InvalidOperationException` with the full known-names list. DI: `AddDefaultLlmGatewayMiddlewareFactory()` / `AddDefaultToolGatewayMiddlewareFactory()`.
  - **`AgentManifestTranslator` gateway ref resolution** — when a manifest carries `llmGatewayRef` or `mcpGatewayRef`, the translator fetches the named config from the registry, instantiates each middleware spec via the factory, and sets `StatefulAgentOptions.GatewayMiddleware` / `ToolGatewayMiddleware` before grain construction. `ToolWorkspacePolicy` spec entries are intercepted before reaching the factory: the translator constructs `ToolWorkspacePolicyMiddleware` directly from the manifest's `WorkspacePolicies` dictionary.
  - **`transport: registered` expansion** — manifest entries with `transport: registered` are resolved from `IMcpServerRegistry` at translation time. Virtual servers (`McpServerManifest.virtualServers`) trigger `VirtualMcpToolSource` construction; physical servers are resolved via `INamedToolSourceProvider` (e.g. a running Python plugin supervisor).
  - **`VirtualMcpToolSource`** — `internal sealed` `IToolSource` that aggregates N upstream `IToolSource` instances. No-projection mode: first-source-wins on tool-name collision (deduplicated by `HashSet<string>`). Projection mode: only explicitly projected tools are exposed; `McpServerToolProjection(Name, From, SourceToolName?)` supports rename — `RenamedTool` inner class wraps upstream, overrides `Name`, delegates all other members.
  - **`CompositionRoot` wiring** — `Vais.Agents.Runtime.Host` now calls all 22 `AddNamed*GatewayMiddleware_*` extensions, `AddDefaultLlmGatewayMiddlewareFactory`, and `AddDefaultToolGatewayMiddlewareFactory` before `AddAgentManifestInstantiator`.
  - **`samples/declarative-agent-mcp-gateways/`** — zero-C# sample. Four YAML manifests (`llm-gateway.yaml`, `mcp-gateway.yaml`, `fetch-server.yaml`, `research-agent.yaml`), `docker-compose.yaml` (vais-runtime + mcp-fetch sidecar), and `README.md` with apply-in-dependency-order quickstart, bad-ref 422 validation, live config update (no redeployment), and full teardown steps.
  - **12 Phase 3 tests** in `Vais.Agents.Runtime.Instantiation.Tests/GatewayPhase3Tests.cs` covering: `DefaultLlmGatewayMiddlewareFactory` name dispatch + unknown-name error, translator LLM/tool gateway pipeline assembly from registry configs, `ToolWorkspacePolicy` sentinel bypass (factory not called), `transport: registered` physical + virtual resolution, `VirtualMcpToolSource` no-projection and projection modes, `McpServerToolProjection` rename.
- **Tool Gateway — Phase 3: five optional Mcp\* plugin packages.**
- **Tool Gateway — Phase 3: five optional Mcp\* plugin packages.** Each package targets `Vais.Agents.Abstractions` only (no `Core` dependency); ships `PublicAPI.Shipped.txt` and is individually packable. All middleware overrides the public `ToolGatewayMiddleware.InvokeAsync` and can be combined in `StatefulAgentOptions.ToolGatewayMiddleware`.
  - **`Vais.Agents.Gateways.McpReliability`** — reliability primitives. `ToolRetryMiddleware` retries failed tool calls with exponential backoff, skipping `ToolDenied`, `CircuitOpen`, and `ToolRateLimitExceeded` outcomes as non-retryable. `ToolTimeoutGuard` enforces a per-dispatch deadline via `Task.WhenAny`, returning `Error = "ToolTimeout"` instead of throwing; outer cancellation propagates normally. `ToolCircuitBreakerMiddleware` tracks failure counts per `WorkspaceId` (lock-based `CircuitState`); after `failureThreshold` consecutive failures the circuit opens for `openDuration`, returning `Error = "CircuitOpen"` to all callers during that window. DI: `AddToolRetryMiddleware`, `AddToolTimeoutMiddleware`, `AddToolCircuitBreakerMiddleware`.
  - **`Vais.Agents.Gateways.McpCache`** — deterministic result cache. `ToolResultCacheMiddleware` short-circuits calls with a cached outcome on hit; on miss calls `next` and stores success outcomes only. Cache key: `{toolName}:{arguments}` (normalized `JsonElement.ToString()`). Tools listed in `excludedTools` always bypass the cache. Cached hits remap the stored outcome's `CallId` to the current call. `InMemoryToolResultCache` (thread-safe `ConcurrentDictionary`). `IToolResultCache` seam for distributed stores. DI: `AddInMemoryToolResultCache`, `AddToolResultCacheMiddleware`.
  - **`Vais.Agents.Gateways.McpGovernance`** — per-workspace governance. `ToolRateLimitMiddleware` enforces a sliding-window per-workspace-per-tool request budget using `IRateLimitStore.RecordAndGetAsync` (from `Vais.Agents.Gateways.Governance`); key pattern `tool:{workspaceId}:{toolName}`, falls back to `_global`. Returns `Error = "ToolRateLimitExceeded"` on breach. `ToolWorkspacePolicyMiddleware` looks up a `WorkspaceToolPolicy` by `AgentContext.WorkspaceId`; absent policies pass through. `WorkspaceToolPolicy` evaluates deny prefixes first, then allow prefixes (empty = all non-denied allowed), then `MinPrivilegeLevel` — callers with numerically higher `PrivilegeLevel` than `MinPrivilegeLevel` are denied (`Platform=0` highest, `Agent=2` lowest). DI: `AddToolRateLimitMiddleware`, `AddToolWorkspacePolicyMiddleware`.
  - **`Vais.Agents.Gateways.McpSecurity`** — security guardrails. `ToolArgumentValidationMiddleware` checks that declared required fields exist in `Arguments`, returning `Error = "ToolDenied"` with the missing-field list without calling `next`. `ToolOutputLengthGuard` rejects (does not truncate) responses exceeding `maxCharacters` with `Error = "ToolOutputTooLarge"`. Both pass error outcomes through unchanged. DI: `AddToolArgumentValidationMiddleware`, `AddToolOutputLengthGuard`.
  - **`Vais.Agents.Gateways.McpTransformation`** — response normalisation. `ToolJsonRepairMiddleware` validates `Result` as JSON; on parse error attempts a structural repair (stub — no-op when repair is not possible); returns the original on failure. `ToolHtmlToMarkdownMiddleware` detects HTML responses by heuristic (starts with `<`, contains tag-like content), strips tags via compiled `Regex`, and decodes HTML entities with `WebUtility.HtmlDecode`. Both middlewares pass error outcomes through unchanged. DI: `AddToolJsonRepairMiddleware`, `AddToolHtmlToMarkdownMiddleware`.
- **Tool Gateway — Phase 2: four reference plugins in `Vais.Agents.Core/Gateway/`.** All ship with `PublicAPI.Unshipped.txt` entries including the public override `InvokeAsync` signatures.
  - `ToolLoggingMiddleware` — emits two `LogLevel.Debug` messages per call: dispatch (tool name + call ID) and outcome (succeeded / error code). Never mutates the outcome. DI: `AddToolLoggingMiddleware()`.
  - `ToolOtelMiddleware` — starts an `Activity` named `tool.gateway/{toolName}` per dispatch on `AgenticDiagnostics.ActivitySource`. Tags: `vais.tool.name`, `vais.tool.call_id`, `vais.workspace.id`. Sets `ActivityStatusCode.Ok` or `ActivityStatusCode.Error`; on exception sets `vais.error.type` to the exception type name and rethrows. No-op when no listener is attached. DI: `AddToolOtelMiddleware()`.
  - `ToolDenyFilterMiddleware` — case-insensitive exact match against a static block list; blocked calls return `Error = "ToolDenied"` without calling `next`. Complementary to `AgentContext.AllowedTools` dynamic allow-list in `DefaultToolCallDispatcher`. DI: `AddToolDenyFilterMiddleware(blockedToolNames)`.
  - `ToolResponseTruncationMiddleware` — truncates results that exceed `maxCharacters` (default 4096), appending `[Truncated: response exceeded N characters]`. Error outcomes are never truncated. DI: `AddToolResponseTruncationMiddleware(maxCharacters)`.
- **Tool Gateway — Phase 1: middleware pipeline in `Vais.Agents.Abstractions` and `Vais.Agents.Core`.** Pluggable cross-cutting behaviour layer that wraps every tool invocation dispatched by `DefaultToolCallDispatcher`.
  - `ToolGatewayMiddleware` (Abstractions) — abstract base class with `public virtual Task<ToolCallOutcome> InvokeAsync(ToolGatewayContext, Func<Task<ToolCallOutcome>>, CancellationToken)`. Public virtual (not protected) so concrete overrides are part of the package's public API surface.
  - `ToolGatewayContext` (Abstractions) — `sealed record(string ToolName, string CallId, JsonElement Arguments, AgentContext AgentContext)`. Carries the full per-call context into every middleware.
  - `IToolResultCache` (Abstractions) — `TryGetAsync` / `SetAsync` seam for deterministic tool-result caching. Separate from `ISemanticCacheStore` (which is LLM-response-level).
  - `StatefulAgentOptions.ToolGatewayMiddleware` — new `IReadOnlyList<ToolGatewayMiddleware>` property. Middleware is applied outermost-first (index 0 = first interceptor). Passed into `DefaultToolCallDispatcher`; the journal replay path bypasses the chain so replayed tool calls are not double-logged or double-counted.
  - `AddToolGatewayMiddleware<T>()` — DI extension for `IServiceCollection` (`Vais.Agents.Core`). Registers `T` as a singleton `ToolGatewayMiddleware`; the manifest translator picks up all registered middleware and prepends them to every declarative agent's tool dispatch chain.
- **LLM Gateway — Phase 4: OSS routing interfaces, OpenAI-compatible transport, and Prometheus metrics.** Native .NET OpenAI-compatible HTTP endpoint backed by the `LlmGatewayMiddleware` chain, replacing LiteLLM as the proxy layer. Three new additions to `Vais.Agents.Abstractions`, three to `Vais.Agents.Core`, and two new packages (`Vais.Agents.Gateways.OpenAiCompat`, `Vais.Agents.Gateways.Prometheus`).
  - **Abstractions — new interfaces:**
    - `IInboundIdentityResolver` — resolves an inbound bearer token to an `AgentContext`. Implement to tie gateway auth to your identity provider.
    - `IModelRouter` — maps a `modelId` string to a `ModelRoute(ICompletionProvider, ModelSpec)`. Implement for custom provider selection logic.
    - `ModelRoute` — `sealed record` pairing a resolved `ICompletionProvider` with its `ModelSpec`. Returned by `IModelRouter.ResolveAsync`.
    - `ModelNotFoundException` — thrown when `IModelRouter` cannot resolve the requested model ID; maps to `404` on the HTTP surface.
    - `IAgentContextSetter` — write-side complement to `IAgentContextAccessor`. `Push(AgentContext)` returns an `IDisposable` that restores the prior context on dispose. `AsyncLocalAgentContextAccessor` now implements both interfaces.
  - **Core — new implementations:**
    - `PassThroughIdentityResolver` — `IInboundIdentityResolver` that always resolves to a default `AgentContext`. Suitable for development and unauthenticated deployments. DI: `AddPassThroughIdentityResolver()`.
    - `InMemoryModelRouter` — `IModelRouter` backed by an `IReadOnlyDictionary<string, ModelRoute>`. Thread-safe; no external service required. DI: `AddInMemoryModelRouter(routes)`.
    - `LlmGatewayPipeline` — public static helper that builds and drives a `LlmGatewayMiddleware` chain outside of `StatefulAiAgent`. `InvokeAsync` for non-streaming calls; `StreamAsync` for streaming with per-delta `OnDeltaAsync` and a final `OnStreamCompleteAsync`. Used internally by `Vais.Agents.Gateways.OpenAiCompat`.
  - **`Vais.Agents.Gateways.OpenAiCompat`** — new package exposing an OpenAI-compatible HTTP surface over any `IModelRouter` + `LlmGatewayMiddleware` chain. Wire with `AddOpenAiCompatGateway()` + `MapOpenAiCompat()`.
    - `POST /v1/chat/completions` — non-streaming (JSON) and streaming (SSE, `text/event-stream`) paths. Bearer token from `Authorization` header is passed to `IInboundIdentityResolver`; resolved context is pushed as the ambient `AgentContext` for the request lifetime.
    - `GET /v1/models` — returns the router's route table as an OpenAI `ModelListResponse`.
    - Error mapping: `401` on identity failure, `404` on unknown model, `422` on missing model field, `429` on `AgentBudgetExceededException`, `400`/`500` for other errors.
    - All request/response DTOs are `internal sealed`; no public DTO surface beyond the two DI/endpoint extensions.
  - **`Vais.Agents.Gateways.Prometheus`** — new package emitting per-call Prometheus metrics via `prometheus-net`. Depends only on `Vais.Agents.Abstractions` + `prometheus-net`; no `Core` dep. Wire with `services.AddLlmPrometheusMiddleware()`.
    - `LlmPrometheusMiddleware` records three metrics per call: `llm_requests_total{model,workspace,status}` (request count; `status` = `success` or `error`), `llm_request_duration_seconds{model,workspace}` (latency histogram), and `llm_tokens_total{model,workspace,type}` (`type` = `prompt` or `completion`). Workspace label sourced from `AgentContext.WorkspaceId`; defaults to `_default`. Two constructors: DI (writes to `Metrics.DefaultRegistry`) and test (accepts an isolated `MetricFactory`).
- **LLM Gateway — Phase 3: five optional plugin packages.** Cross-cutting LLM middleware that plugs into any `StatefulAiAgent` via `GatewayMiddleware` without touching provider or agent code. All packages target `Vais.Agents.Abstractions` only (no `Core` dep at runtime); each ships `PublicAPI.Shipped.txt` and is individually packable.
  - **`Vais.Agents.Gateways.Fallback`** — `LlmFallbackMiddleware` tries providers from an `IFallbackProviderPool` in order, falling back to the next on any non-cancellation exception. Streaming path commits to the first provider that delivers a successful delta (no retry after stream starts). `LlmLoadBalancingMiddleware` distributes calls round-robin via `Interlocked.Increment`. `InMemoryFallbackProviderPool` for in-process pools. DI: `AddLlmFallbackMiddleware(pool)` / `AddLlmLoadBalancingMiddleware(pool)`.
  - **`Vais.Agents.Gateways.SemanticCache`** — `LlmSemanticCacheMiddleware` short-circuits repeated LLM calls by returning cached `CompletionResponse` objects. Cache key is the last user turn's text. Streaming path collects the full stream before caching; cache hits yield a single synthetic delta. `InMemorySemanticCacheStore` (ConcurrentDictionary, thread-safe). `ISemanticCacheStore` seam for custom vector-similarity stores. DI: `AddLlmSemanticCacheMiddleware()`.
  - **`Vais.Agents.Gateways.Governance`** — `LlmRateLimitMiddleware` enforces per-key sliding-window request and token budgets. Throws `AgentBudgetExceededException` when a limit is exceeded. `InMemorySlidingWindowRateLimitStore` accepts an optional `TimeProvider` for deterministic tests. `IRateLimitStore` seam for Redis / distributed stores. `RateLimitOptions` with `MaxRequestsPerWindow`, `MaxTokensPerWindow`, and `Window`. DI: `AddLlmRateLimitMiddleware(options)`.
  - **`Vais.Agents.Gateways.StructuredOutput`** — `LlmJsonOutputMiddleware<T>` validates each LLM response as `T` via `System.Text.Json`. Throws `AgentGuardrailDeniedException(GuardrailLayer.Output, ...)` on parse failure. Runs on both non-streaming (`InvokeAsync`) and streaming (`OnStreamCompleteAsync`) paths.
  - **`Vais.Agents.Gateways.Testing`** — `LlmMockMiddleware` intercepts LLM calls and returns pre-queued `CompletionResponse` objects. Dequeues one response per call (non-streaming and streaming). Throws `InvalidOperationException` when the queue is exhausted. Intended for unit tests of agent logic without a real LLM provider.
- **LLM Gateway — Phase 2: four reference plugins in `Vais.Agents.Core`.** All middleware operates on both non-streaming and streaming paths and is wired as gateway middleware.
  - `LlmLoggingMiddleware` — logs each LLM request and response at `LogLevel.Debug`. Request log includes turn count and tool count; response log includes prompt + completion token counts. DI: `AddLlmLoggingMiddleware()`.
  - `LlmUsageMiddleware` — reports token usage to `IUsageSink` after every non-streaming turn and after every stream drains. Captures wall-clock timing and propagates full `AgentContext` fields (`AgentName`, `UserId`, `TenantId`, `CorrelationId`, `WorkspaceId`). DI: `AddLlmUsageMiddleware()`.
  - `LlmOtelMiddleware` — emits an `Activity` span per LLM call (`llm.completion` non-streaming, `llm.completion.stream` streaming) using `AgenticDiagnostics.ActivitySource`. Tags request with turn count and context fields; tags response with model ID and token counts. Sets `ActivityStatusCode.Error` on provider exceptions. Safe when no listener is attached (StartActivity returns null — zero overhead). DI: `AddLlmOtelMiddleware()`.
  - `LlmPromptEnrichmentMiddleware` — appends or prepends a fixed string to every request's system prompt (both paths). Useful for injecting platform-level safety instructions without touching individual agent manifests. Passes the original request object through unchanged when both prefix and suffix are empty.
- **LLM Gateway — Phase 1: middleware pipeline in `Vais.Agents.Abstractions` and `Vais.Agents.Core`.** Pluggable cross-cutting behaviour layer that sits between the resilience pipeline and the completion provider, operating on both the non-streaming and streaming paths.
  - `LlmGatewayMiddleware` (Abstractions) — abstract base class implementing both `IAgentFilter` and `IStreamingAgentFilter`. Override any of `InvokeAsync`, `InvokeStreamAsync`, `OnDeltaAsync`, `OnStreamCompleteAsync`. Unoverridden methods pass through transparently.
  - `StatefulAgentOptions.GatewayMiddleware` — new `IReadOnlyList<LlmGatewayMiddleware>` property. Gateway middleware is prepended to the filter + streaming-filter chains in registration order (index 0 = outermost). Middleware added here does not participate in Polly's resilience scope.
  - `AddLlmGatewayMiddleware<T>()` — DI extension for `IServiceCollection` (`Vais.Agents.Core`). Registers `T` as a singleton `LlmGatewayMiddleware`; the manifest translator picks up all registered middleware and prepends them to every declarative agent's filter chains.

- **`vais invoke-graph --output state`** — new output mode that serialises the final state bag as indented JSON to stdout. Useful for piping state into subsequent tools or scripts. Existing `text` and `json` modes are unchanged.
- **`PythonPluginsReadyCheck`** — new `IHealthCheck` registered on `/readyz` (tag `"ready"`) when `VAIS_PYTHON_PLUGINS_DIRECTORY` is set. Reports `Healthy` when all Python plugins reach `Ready`; `Degraded` while any plugin is still `Loading`/`Restarting`; `Unhealthy` when any plugin is `Unavailable`. Prevents Kubernetes from routing traffic to a pod whose Python plugins have not completed startup.
- **`vais plugin-status`** — new CLI command listing all loaded plugins (assembly + Python subprocess) in a table showing name, kind, state, API version, handler / tool names, PID, and last stderr snippet for failed Python plugins. Supports `-o json` and `-o yaml` output modes.
- **`GET /v1/plugins` extended for Python plugins.** Response now includes Python subprocess plugins alongside .NET assembly plugins. Each entry carries new fields: `kind` (`Assembly`/`Python`), `state` (`Loading`/`Ready`/`Restarting`/`Unavailable`), `processId`, `toolNames` (Python MCP tools), and `lastErrorSnippet` (last stderr lines when a Python plugin fails).
- **`IAgentControlPlaneClient.ListPluginsAsync`** — new typed HTTP client method for `GET /v1/plugins`. Default interface implementation returns an empty response so mock implementations need no changes.
- **`LoadedPythonPlugin.LastErrorSnippet`** — new `init` property exposing the last 3 stderr lines from the most recent subprocess spawn. Surfaced in `/v1/plugins` and `vais plugin-status` so operators can diagnose Python plugin import failures without separately streaming subprocess logs.
- **`POST /v1/graphs/validate`** — new stateless dry-run endpoint (v0.38). Accepts an `AgentGraph` manifest in the request body and returns `GraphValidationResult` with `valid` (bool) and `errors` (string list) — always `200 OK`. Performs structural checks via `AgentGraphManifestValidator` then runtime-context checks: Code-kind nodes are cross-referenced against `IPluginHandlerRegistry`; Agent-kind nodes are cross-referenced against `IAgentRegistry`. Surfaces at CI time the class of errors that previously only appeared at invoke time.
- **`vais graph-validate -f manifest.yaml`** — new CLI command driving `POST /v1/graphs/validate`. Prints `valid <id>` on success; lists errors on failure. Exit code 0 = valid, 1 = errors. Supports `-o json` / `-o yaml` for structured output.
- **`IAgentControlPlaneClient.ValidateGraphAsync`** — new typed HTTP client method for `POST /v1/graphs/validate`. Default interface implementation returns a passing result so mock implementations need no changes.
- Docs: `docs/guides/run-python-plugins-on-windows.md` — new guide documenting Git Bash MSYS path-conversion footguns (`interpreter` path, volume mounts, CLI arguments) and the fixes (`//` prefix, `MSYS_NO_PATHCONV=1`, Windows-native paths, PowerShell).
- Docs: `docs/guides/wrap-third-party-tools.md` — new guide covering DI introspection of `INamedToolSourceProvider` registrations and the override/wrap pattern for customising third-party tool sources.
- `AssemblyPluginLoader.ResolvePrimaryAssembly` now resolves the primary DLL when the folder contains multiple assemblies (e.g. `CopyLocalLockFileAssemblies=true`) by trying a suffix match (`<Something>.<PluginName>.dll`) after the exact-name match fails. Previously returned null for all multi-DLL folders.
- `AssemblyPluginLoader.TryRegisterHandler` now falls back to a simple-name scan when `assembly.GetType(fullName)` returns null, so `[VaisPlugin(..., "WeatherAgent")]` resolves to `MyApp.WeatherAgent.WeatherAgent` and logs the full CLR name as a hint. Previously the plugin was silently skipped.
- `PythonSubprocessSupervisor` now buffers the last 20 stderr lines per subprocess spawn cycle and includes them in both the MCP handshake-timeout warning and any other handshake failure (`Exception`), so the Python traceback is visible without separately streaming subprocess logs. When stderr contains `ImportError` or `AttributeError`, a targeted `LogError` is emitted recommending moving module-level `AgentDefinition` construction inside a function.
- `PluginManifestConsistencyCheck` — new `IHostedService` registered in `CompositionRoot` that walks every registered `AgentManifest` at startup and logs `LogError` for each manifest whose `Handler.TypeName` does not resolve to a loaded plugin handler. Surfaces mis-deployed plugins at boot time rather than at first invocation.
- `services.AddHttpClient()` registered in `CompositionRoot.ConfigureServices` so `IHttpClientFactory` is available to plugin handler constructors without any extra registration. Previously documented as available but never wired.
- `FallbackUvSync = true` in `PythonPluginLoaderOptions` when `VAIS_MODE=localhost` — automatically runs `uv sync --frozen` inside the plugin directory when `.venv/` is absent, removing the manual setup step for new contributors.
- Startup warning emitted to stderr when `VAIS_LANGFUSE_PROJECT` is set but neither `VAIS_OTEL_ENDPOINT` nor `VAIS_OTEL_CONSOLE` is configured, so the "Langfuse traces are silently dropped" footgun is surfaced at boot time.
- `GraphEventRenderer` default case now applies `EscapeMarkup` to the event type name, preventing a potential Spectre.Console crash if an unknown `AgentGraphEvent` subtype with brackets in its name is received.
- `OpenAIModelProviderFactory` now honours `ModelSpec.BaseUrlRef`: when set, the resolved value is used as the API endpoint, enabling any OpenAI-compatible service (local models, proxies, SGR Agent, etc.) to be consumed without additional code. Behaviour is unchanged when `BaseUrlRef` is absent.
- `PythonAgentShim` — `IAiAgent` backed by a Python subprocess via the new `vais/agent.*` JSON-RPC MCP extension (v0.24). Supports opaque state round-trips so Python agents maintain their own internal state across turns without the .NET side parsing it.
- `IOpaqueStateCarrier` interface wired through `AiAgentGrain` — the grain persists the opaque blob alongside history so Python agent state survives silo restarts.
- `AgentInvokeRequest` / `AgentInvokeResponse` JSON-RPC protocol types and `IPythonAgentChannel` abstraction for the Python subprocess channel.

### Changed
- **Breaking:** `IAgentManifestTranslator.TranslateForGrain` signature changed from `StatefulAgentOptions TranslateForGrain(IServiceProvider, string)` (sync) to `ValueTask<StatefulAgentOptions> TranslateForGrain(IServiceProvider, string, CancellationToken)` (async). Update call sites from `translator.TranslateForGrain(sp, id)` to `await translator.TranslateForGrain(sp, id, ct)`.
- **Breaking:** `ConfigureAgentGrains` now accepts `Func<IServiceProvider, string, CancellationToken, ValueTask<StatefulAgentOptions>>?` instead of `Func<IServiceProvider, string, StatefulAgentOptions>?`. The DI-registered type changes from `Func<string, StatefulAgentOptions>` to `Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>`. Update lambdas from `(sp, id) => new StatefulAgentOptions { ... }` to `(sp, id, ct) => ValueTask.FromResult(new StatefulAgentOptions { ... })`.
- **Breaking:** `AiAgentGrain` constructor parameter `optionsFactory` type changed from `Func<string, StatefulAgentOptions>?` to `Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>?`.
- `AiAgentGrain.OnActivateAsync` is now truly `async` — it awaits the options factory directly instead of blocking via `GetAwaiter().GetResult()`. This eliminates the Orleans grain activation deadlock that caused 2-minute `ResponseTimeout` failures on first invocation.

### Added (observability, continued)
- **Token counts and model on `python.agent.ask` spans.** `PythonAgentShim.AskAsync` sets `gen_ai.response.model`, `gen_ai.usage.input_tokens`, and `gen_ai.usage.output_tokens` on the `python.agent.ask` activity from `AgentInvokeResponse.Usage`. Langfuse can display token counts and calculate costs for Python agent invocations when the plugin returns usage data.
- **Researcher plugin returns token usage.** `langgraph-researcher-live/agent.py` now collects per-invocation token counts via a `_TokenUsageCallback` (LangChain `BaseCallbackHandler`) passed through `_compiled.invoke(config={"callbacks": [tracker]})`, and returns them as `AgentUsage` in `AgentResponse`. The callback accumulates `usage_metadata` from every `AIMessage` emitted by any node in the graph, so multi-node graphs are covered automatically.
- **P4-B: W3C traceparent propagation to Python agents.** `PythonAgentShim` now passes the W3C `traceparent` (the `python.agent.ask` span's Activity ID) in `AgentInvokeRequest.context["traceparent"]`. The `vais-agent-sdk` runner reads it on every `vais/agent.invoke` and `vais/agent.stream` call, calls `opentelemetry.context.attach()` before invoking the user callable, and `detach()` after, so all OTel spans emitted by the Python agent (LLM calls, LangGraph node transitions, tool calls) are automatically parented to the correct `python.agent.ask` span in Langfuse. `setup_otel()` is called once at process start; reads `OTEL_EXPORTER_OTLP_ENDPOINT` from the environment. `opentelemetry-sdk` and `opentelemetry-exporter-otlp-proto-http` added as required deps in `vais-agent-sdk/pyproject.toml` — picked up automatically by the Dockerfile `uv pip install /sdk` step.
- **`tool.call/{name}` spans for every tool invocation.** `DefaultToolCallDispatcher` now wraps `tool.InvokeAsync` in a child span of the ambient `chat` span, tagged with `vais.tool.name`, `vais.tool.call_id`, `gen_ai.prompt` (JSON arguments), and `gen_ai.completion` (result text). Error path sets `ActivityStatusCode.Error` and `error.type`. Reuses the existing `"Vais.Agents"` ActivitySource — no new source registration needed. Covers both SK and MAF backends: `MafCompletionProvider` uses `UseProvidedChatClientAsIs = true`, which disables MAF's `FunctionInvokingChatClient` middleware and routes tool calls through `StatefulAiAgent`'s dispatcher, same as SK. Full tree in Langfuse: `graph.run → graph.node → grain.ask → chat → tool.call/SearchWeb → …`
- `AgenticTags.ToolName` (`"vais.tool.name"`) and `AgenticTags.ToolCallId` (`"vais.tool.call_id"`) — new tag constants for tool-call spans.

### Fixed
- **`grain.activate` orphan traces in Langfuse.** `AiAgentGrain.OnActivateAsync` now only starts the `grain.activate` span when `Activity.Current != null`. Previously, Orleans always fired `OnActivateAsync` on the grain scheduler with no ambient trace context, producing a disconnected root trace per grain activation (four per pipeline run). Span is silently skipped when there is no parent.
- **Test spans polluting the demo Langfuse project.** Added `vais.runsettings` (repo root) that sets `OTEL_TRACES_EXPORTER=none`, `OTEL_METRICS_EXPORTER=none`, and `OTEL_LOGS_EXPORTER=none`. `Directory.Build.props` auto-wires this file for all `IsTestProject` projects via `<RunSettingsFilePath>`, so host-level `OTEL_EXPORTER_OTLP_ENDPOINT` env vars no longer leak into test runner processes.
- **`invoke-graph --stream` crashes on `StateUpdated` events.** `GraphEventRenderer` rendered changed-key names as `[key1, key2]` — Spectre.Console interpreted the brackets as a markup style tag and threw "Could not find color or style". Escaped to `[[key1, key2]]` so the keys render as plain text.
- **`finalState: null` in graph invoke response.** `InProcessGraphOrchestrator.StreamAsync` accumulates state in an internal copy that was never surfaced back to the caller. Added `IReadOnlyDictionary<string, JsonElement>? FinalState` to `GraphCompleted`; the orchestrator now snapshots the terminal state bag into the event. `DrainInvokeAsync` and `DrainResumeAsync` in `AgentGraphLifecycleManager` now capture `FinalState` from the event instead of returning the original (unchanged) initial state.
- **Plugin ABI mismatch log was not actionable.** `AssemblyPluginLoader.LoadViaAttribute` emitted a generic warning on version mismatch without telling the author how to fix it. Warning now includes the exact `[assembly: VaisPlugin(targetApiVersion: "...")]` attribute change and rebuild instruction needed to resolve the mismatch.
- **OTLP traces not sent to Langfuse.** Two root causes: (1) `CompositionRoot.ConfigureObservability` set `o.Endpoint` programmatically — when the endpoint is set in code the .NET OTEL SDK does NOT append the signal-specific path suffix (`/v1/traces`) and all export requests hit the base path which returns 404; (2) `OTEL_EXPORTER_OTLP_HEADERS` was not read explicitly, relying on the SDK's env-var auto-read which was blocked by the code-level configure action. Fix: removed `o.Endpoint` from the configure action (letting the SDK read `OTEL_EXPORTER_OTLP_ENDPOINT` and correctly append `/v1/traces`); added `OtelHeaders` to `RuntimeOptions` and explicitly set `o.Headers` in the configure action. Also added `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` to `docker-compose.demo.yml` for clarity.
- **Orleans grain activation deadlock.** `OnActivateAsync` → `TranslateForGrain` → `GetAwaiter().GetResult()` blocked the Orleans single-threaded scheduler while waiting for an inner grain RPC whose continuation was posted back to that same blocked scheduler — deadlock held for exactly `SiloMessagingOptions.ResponseTimeout` (2 minutes). Root cause: `OrleansAgentRegistry` methods lacked `ConfigureAwait(false)` and `TranslateForGrain` used a sync-over-async bridge. Both issues resolved.
- Added `ConfigureAwait(false)` to four grain-to-grain calls in `OrleansAgentRegistry` (`ListAsync`, `GetAsync`, `RegisterAsync`) — independent fix that unblocks the deadlock even with the old sync bridge.

### Added (observability)
- `OrleansDiagnostics` — new public class in `Vais.Agents.Hosting.Orleans` exposing `ActivitySourceName = "Vais.Agents.Hosting.Orleans"` and a shared `ActivitySource` for Orleans grain spans.
- `AiAgentGrain` now emits `grain.activate` and `grain.ask` OTel spans tagged with `vais.agent.name`. Both spans are automatically picked up by `AddAgenticInstrumentation` — no consumer changes required.
- `AiAgentGrain` now logs structured events at `Debug`/`Information`/`Error` level (category `Vais.Agents.Hosting.Orleans.AiAgentGrain`): grain activating, options factory elapsed time, activation mode (`plugin`/`declarative`), and per-ask elapsed time. Factory exceptions are logged at `Error` with elapsed milliseconds — enough to distinguish an Orleans deadlock (factory blocked for ~120 000 ms) from a slow but successful cold-start. Enable `Debug` in appsettings to see per-method timing; `Information` is on by default.
- `AddAgenticInstrumentation(TracerProviderBuilder)` now also registers the `"Vais.Agents.Hosting.Orleans"` source in addition to `"Vais.Agents"`.
- **Langfuse observability enrichment.** Graph executions, agent I/O, and trace hierarchy are now visible in Langfuse:
  - `InProcessGraphOrchestrator` emits `graph.run` (root per invocation) and `graph.node` (one per node) OTel spans via a new `"Vais.Agents.Core.Graph"` `ActivitySource`. Tags: `graph.id`, `graph.version`, `graph.run_id`, `graph.entry`, `graph.node.id`, `graph.node.kind`, `vais.agent.name` (on Agent-kind nodes), `langfuse.trace.name`, `langfuse.session.id` (from `AgentContext.CorrelationId` / `UserId`).
  - `OrleansOutgoingActivityFilter` — new `IOutgoingGrainCallFilter` that writes the current W3C traceparent into `Orleans.RequestContext` before each grain call. `AiAgentGrain.AskAsync` reads it back and parents its `grain.ask` span to the caller's `graph.node` span. Result: Langfuse renders the full tree `graph.run → graph.node → grain.ask → chat`.
  - `StatefulAiAgent` `chat` span now carries `gen_ai.prompt` (user message) and `gen_ai.completion` (final assistant text), and `langfuse.observation.type=generation` so Langfuse renders it in its generation view with model, tokens, and cost.
  - `PythonAgentShim.AskAsync` now emits a `python.agent.ask` span (source `"Vais.Agents.Runtime.Plugins.Python"`) with `gen_ai.prompt` and `gen_ai.completion`. Registered via `AddAgenticInstrumentation`.
  - `OTEL_SERVICE_NAME=vais-oss-runtime` added to `docker-compose.demo.yml` — fixes `service.name: unknown_service:dotnet` in Langfuse resource attributes.

---

## [0.23.0-preview] — 2026-04-24

### Added
- **Python plugin pillar.** MCP stdio subprocess hosting for Python agents. Deploy a `pyproject.toml`-based Python package alongside the runtime; the host spawns it, handshakes via MCP `initialize` + `tools/list`, and restarts on crash with exponential backoff.
- `PythonSubprocessSupervisor` — per-plugin state machine: spawn → MCP handshake → Ready → restart loop. Configurable handshake timeout, invoke timeout, and restart policy (`Never` / `ExponentialBackoff`).
- `PythonPluginHostService` — `IHostedService` that scans the plugins directory, starts supervisors in parallel, and exposes `IPythonPluginHost` for introspection.
- `PythonPluginScanner` / `PluginYamlDeserializer` — discovers Python plugin packages via `pyproject.toml` `[tool.vais.plugin]` metadata.
- `PythonPluginDescriptor` record — captures plugin name, directory, interpreter path, entrypoint, ABI version, handshake/invoke timeouts, restart policy, declared tools, and secret refs.
- `INamedToolSourceProvider` — extension point for per-named-server `IToolSource` lookup. Implemented by `PythonPluginHostService` so MCP tool references in agent manifests resolve to the running subprocess client.
- `PythonPluginUrns` — structured URN constants for all plugin lifecycle events (`load-failed`, `handshake-timeout`, `exited`, `unavailable`, `abi-mismatch`, `ambiguous-folder`).
- ABI version negotiation: plugin declares `target_api_version` in `pyproject.toml`; host rejects mismatched versions with `abi-mismatch` URN.
- `AddPythonPlugins` DI extension wiring `PythonPluginHostService` + `INamedToolSourceProvider` into the silo.
- `PluginAgentResearchPlanner` sample — LangGraph-based research-planner Python agent deployed as a plugin, with `pyproject.toml`, venv setup, and `docker-compose` overlay.
- `PythonEchoWireTests` — wire-level integration tests for `PythonSubprocessSupervisor` including timeout, asyncio dispatch, and restart scenarios.
- Concept doc `docs/concepts/polyglot-agents.md` and guide `docs/guides/package-a-python-agent.md`.
- `ManifestInstantiationUrns.McpServerUnavailable` and `McpToolNotFound` URN constants.

### Changed
- `AiAgentGrainState` gains `OpaqueState` property for persisting plugin-agent opaque blobs across silo restarts.

---

## [0.21.0-preview] — 2026-04-22

### Added
- **Streaming journal replay** (`ReplayMode.Full`). `OrleansAgentJournal.ReadAsync` replays persisted `JournalEntry` records as an `IAsyncEnumerable<JournalEntry>`, including `CompletionDeltaRecorded` entries for streaming deltas.
- `CompletionDeltaRecorded` journal entry kind — records each streaming completion chunk with model id, prompt/completion tokens, and text delta.
- **Per-attempt retry telemetry.** `StatefulAiAgent`'s resilience pipeline now emits attempt-number tags on every retry so dashboards can bucket retries vs. first-try calls.
- **A2A cross-runtime graph nodes.** `RemoteAgentNode` resolves the target runtime URL from the graph manifest's `A2ARemoteAgents` declarations and calls it over the A2A wire protocol, enabling graph steps to fan out to agents in other Vais.Agents clusters or third-party A2A endpoints.
- **OIDC/OAuth 2.0 token exchange for cross-runtime identity propagation.** Three propagation modes: `Forward` (pass the inbound bearer token), `ServiceAccount` (use a pre-configured client-credential token), and `TokenExchange` (RFC 8693 subject-token exchange). Configured per `RemoteRuntime` entry in `Vais:RemoteRuntimes`.

---

## [0.20.0-preview] — 2026-04-21

### Added
- **Cross-runtime graph refs.** Agent graph manifests can reference agents on remote Vais.Agents runtimes (or any A2A-compatible endpoint). `runtimeUrl` field on `AgentNodeRef` selects the target cluster.
- `runtimeUrl` propagation through all manifest loaders (JSON, YAML, CRD schema).
- `AgentRemoteInvoker` service + typed HTTP client for outbound cross-runtime A2A calls.
- Concept doc + guide for cross-runtime graphs.

---

## [0.19.0-preview] — 2026-04-21

### Added
- **Agent graph as a first-class deployable.** `AgentGraphManifest` can be registered via `vais apply -f` and stored in Orleans grain storage (`OrleansAgentGraphRegistry`) so graph definitions survive silo restart.
- `IAgentGraphRegistry` / `OrleansAgentGraphRegistry` — durable graph manifest storage, mirroring `IAgentRegistry` for agent manifests.
- CRD schema for `AgentGraph` Kubernetes custom resource.
- Helm chart additions for graph registry grain storage.

---

## [0.18.0-preview] — 2026-04-21

### Added
- **Plugin loader wired into `Runtime.Host`.** `CompositionRoot` now discovers and loads code-authored plugin assemblies (`.dll` files in a configurable plugin directory) at startup via `AssemblyLoadContext` isolation.
- `PythonPluginsDirectory` runtime option — enables the Python plugin scan path when set.
- Plugin agent factory registration flows through `IPluginHandlerRegistry` into `AgentManifestTranslator`.

---

## [0.17.0-preview] — 2026-04-21

### Added
- **Declarative agent translator (Pillar B).** `AgentManifestTranslator` translates a stored `AgentManifest` into `StatefulAgentOptions`, resolving model provider, system prompt (inline / template-ref / file-ref), static tools, MCP tools, A2A remote agents, and guardrails.
- `IAgentManifestTranslator` — interface with `TranslateAsync` + `TranslateForGrain` + `InvalidateAsync`.
- `ICompletionProviderPool` — memoised provider pool; shares a single SDK client across all activations of the same `ModelSpec`.
- Per-agent provider resolution: each agent grain gets the provider declared in its manifest's `ModelSpec` rather than a silo-wide singleton.
- `ConfigureAgentGrains` extension wired to the translator in `CompositionRoot` — grain activation now reads the manifest and instantiates the correct provider automatically.
- Builtin guardrail factories: `LengthCap`, `RegexAllowlist`, `RegexDenylist` (input), `LlmAsJudge` (output).
- `FileSystemPromptFileLoader` and `IPromptTemplateRegistry` for system-prompt resolution.
- `ManifestInstantiationUrns` — structured error URNs for all translation failure modes.
- `TranslatorInvalidationHook` — invalidates the translator cache on `UpdateAsync` / `EvictAsync` so the next grain activation picks up the new manifest.

### Changed
- `AiAgentGrain` now derives its `ICompletionProvider` from the per-agent options supplied by the translator (v0.17 Pillar B wire-through) rather than requiring a silo-wide `ICompletionProvider` registration.

---

## [0.16.0-preview] — 2026-04-21

### Added
- **`Vais.Agents.Runtime.Host`** — the deployable runtime container entrypoint. Hosts an Orleans silo, exposes the agent control-plane HTTP API, and wires all pillars together via `CompositionRoot`.
- `RuntimeOptions` — typed configuration model for the runtime container (Orleans connection strings, plugin directories, remote runtime URLs, OPA endpoint, etc.).
- Docker image build (`src/Vais.Agents.Runtime.Host/Dockerfile`).
- `docker-compose.localhost.yml` base recipe + `opa`, `langfuse`, `otel`, `clustered` overlays.
- Helm chart `deploy/helm/vais-agents-runtime/`.
- `CONTRIBUTING.md`, `SECURITY.md`, `CODE_OF_CONDUCT.md`.

---

## [0.15.0-preview] — 2026-04-20

### Added
- **`vais` CLI** (`Vais.Agents.Cli`). Subcommands: `apply`, `get`, `delete`, `invoke`, `logs`, `graph apply/get/delete/invoke`.
- `vais apply -f <manifest.yaml>` — deploy an agent or graph manifest to a running runtime.
- `vais invoke <agent-id> "<message>"` — send a chat turn and stream the reply.
- Shell completions for `bash`, `zsh`, `fish`, `PowerShell`.
- `docs/reference/cli.md` — full subcommand reference.

---

## [0.14.0-preview] — 2026-04-20

### Added
- **OPA policy-engine pillar.** `IAgentPolicyEngine` backed by Open Policy Agent; evaluates `allow` decisions for every agent invocation, tool call, and graph step.
- `OpaAgentPolicyEngine` — HTTP client to an OPA sidecar; bundles the built-in Rego policy bundle.
- `AgentLifecycleManager` policy enforcement: `CreateAsync` / `UpdateAsync` / `EvictAsync` check `allow` before mutating registry state.
- `AddOpaAgentPolicyEngine` DI extension; `OpaOptions` configuration model.
- `docs/concepts/policy.md` and Helm sidecar values for OPA.

---

## [0.13.0-preview] — 2026-04-20

### Added
- **Kubernetes operator pillar** (`Vais.Agents.Control.KubernetesOperator.Host`). Watches `AgentManifest` and `AgentGraph` CRDs; reconciles the agent registry and graph registry to match declared state.
- `AgentManifestReconciler` and `AgentGraphReconciler` — idempotent reconcile loops using KubeOps.
- CRD YAML definitions for `AgentManifest` and `AgentGraph` (v1alpha1).
- Helm chart `deploy/helm/vais-agents-operator/`.
- `deploy/crds/` — standalone CRD install for non-Helm environments.
- `docs/guides/deploy-the-operator.md`.

---

## [0.12.0-preview] — 2026-04-20

### Added
- **SSE streaming invoke** (`POST /v1/agents/{id}/invoke/stream`). Server-Sent Events endpoint that streams `delta` events as the agent generates tokens, followed by a `done` event.
- `StreamAsync` on `IAiAgent` / `StatefulAiAgent` — yields `AgentStreamChunk` items; backed by provider-native streaming (SK / MAF).
- Streaming wire format: newline-delimited `data: {...}` events per SSE spec.
- `OrleansAgentRuntime.StreamAsync` grain method with `GrainCancellationToken` for SSE disconnect propagation.

---

## [0.11.0-preview] — 2026-04-20

### Added
- **OpenAPI spec** auto-generated from the HTTP control-plane endpoints; exposed at `/openapi/v1.json` and `/swagger`.
- **Idempotency-Key middleware** (`X-Idempotency-Key` header). Duplicate requests within the TTL window return the cached response; in-flight duplicates wait and share the result.
- `IIdempotencyStore` + `OrleansIdempotencyStore` — durable idempotency record storage backed by `IIdempotencyKeyGrain`.
- `AddOrleansIdempotencyStore` DI extension; `IdempotencyOptions` configuration model.
- `docs/reference/problem-details-urns.md` — full URN taxonomy for all Problem Details error types.

---

## [0.10.0-preview] — 2026-04-20

### Added
- **Filter + resilience pipeline on `StreamAsync`.** `IAgentFilter`, `IInputGuardrail`, `IOutputGuardrail`, and `IToolGuardrail` now apply to streaming paths as well as request/response.
- `Polly`-backed resilience pipeline on `StatefulAiAgent` — configurable retry, circuit-breaker, and timeout policies via `StatefulAgentOptions.ResiliencePipeline`.
- `IUsageSink` — callback for token usage reporting per turn; receives prompt + completion counts.
- `AgentBudget` — `MaxTurns` and `MaxTokens` hard caps enforced inside the agent loop; raises `BudgetExceededException`.

---

## [0.9.0-preview] — 2026-04-20

### Added
- **Agent graph orchestration pillar.** `IAgentGraph<TState>` — a DAG of agent nodes with conditional routing, parallel fan-out, and human-in-the-loop interrupt/resume.
- `InProcessGraphOrchestrator` — runs a graph to completion or to an `Interrupt` in-process; persists checkpoints via `IGraphCheckpointer`.
- `OrleansCheckpointer` — durable graph checkpoint storage backed by `IGraphCheckpointGrain`.
- `AgentGraphManifest` — YAML/JSON declarative format for graph topology.
- `GraphInterrupted` / `ResumeAsync` for human-in-the-loop patterns.
- `AddOrleansGraphCheckpointer` DI extension.
- `docs/concepts/graphs.md`.

---

## [0.8.0-preview] — 2026-04-19

### Added
- **A2A inbound server pillar.** `AddA2AAgentServer` mounts a standards-compliant [Agent-to-Agent (A2A) protocol](https://github.com/google-a2a/A2A) endpoint on the HTTP server; any A2A-capable client can discover and invoke registered agents.
- `OrleansTaskStore` — durable A2A task storage (`ITaskStore`) backed by `IA2ATaskGrain`; tasks survive silo restart.
- `AddOrleansA2ATaskStore` DI extension.
- `docs/concepts/a2a.md` and interoperability guide.

---

## [0.7.0-preview] — 2026-04-19

### Added
- **MCP server pillar** (`Vais.Agents.McpServer`). Exposes registered agents as MCP tools over stdio transport, streamable-HTTP transport, or both simultaneously.
- JWT dual-headed auth: accepts both MCP-native `Authorization` headers and the control-plane's existing JWT bearer tokens.
- `list_resources` / `read_resource` MCP endpoints — agents as named resources with history content.
- `McpAgentServer` builder + `AddMcpAgentServer` DI extension.
- `docs/concepts/mcp-server.md`.

---

## [0.6.0-preview] — 2026-04-19

### Added
- **Agent control-plane HTTP server** (`Vais.Agents.Runtime.Server`). REST endpoints: `POST /v1/agents` (apply), `GET /v1/agents/{id}`, `DELETE /v1/agents/{id}`, `POST /v1/agents/{id}/invoke`, `GET /v1/agents` (list), and graph equivalents.
- JWT authentication middleware; `IAgentContextAccessor` extracts tenant/user/correlation from the token into `AgentContext`.
- `IAuditLog` + `LoggerAuditLog` — structured audit events for every create/invoke/delete lifecycle step.
- JSON and YAML manifest loaders (`IManifestLoader`) with shared JSON-Schema validation.
- `AgentLifecycleManager` — orchestrates registry + runtime + policy across create/update/evict.
- Problem Details error shaping with `urn:vais-agents:*` URN types for all failure modes.
- Typed HTTP client (`AgentControlPlaneClient`) for .NET consumers of the control-plane API.

---

## [0.5.0-preview] — 2026-04-19

### Added
- **Durable journal pillar.** `IAgentJournal` records every tool call (name, arguments, result, call-id, timestamp) to a persistent log; `OrleansAgentJournal` is the Orleans-backed implementation.
- `IAgentRunJournalGrain` / `AgentRunJournalGrain` — grain storing `JournalEntry` records per run-id.
- `RunId` stamped on every agent run; threaded through `DefaultToolCallDispatcher` so all tool calls in a run share the id.
- `ResumeAsync(runId)` — restores the agent to its end-of-turn state from a prior run by replaying the journal.
- `AddOrleansAgentJournal` DI extension; `OrleansAgentJournal` wired into `CompositionRoot`.

---

## [0.4.0-preview] — 2026-04-19

### Added
- Initial public documentation set (`docs/`) covering concepts, getting-started guides, reference pages, ADRs, and roadmap.
- 20 samples under `samples/` covering every pillar through v0.4.
- `samples/README.md` learning-path table.

---

## [0.3.0-preview] — 2026-04-19

### Added
- Package rename: all packages migrated from `Vais2.Agents.*` to `Vais.Agents.*` namespace.
- `Vais.Agents.Abstractions` — core contracts: `IAiAgent`, `IAgentRuntime`, `IAgentRegistry`, `IAgentSession`, `ChatTurn`, `AgentManifest`, and all extension-point interfaces.
- `Vais.Agents.Core` — `StatefulAiAgent`, `InMemoryAgentRuntime`, `InMemoryAgentRegistry`, `DefaultToolCallDispatcher`.
- `Vais.Agents.Hosting.Orleans` — `AiAgentGrain`, `OrleansAgentRuntime`, `OrleansAgentRegistry`, `OrleansAgentSession`, and all supporting grains and surrogates.
- `Vais.Agents.Ai.SemanticKernel` + `Vais.Agents.Ai.MicrosoftAgentFramework` — completion provider adapters.
- `Microsoft.CodeAnalysis.PublicApiAnalyzers` enabled across all packable projects.
- Central Package Management (`Directory.Packages.props`).
- `AGENTS.md` — AI assistant briefing for this repository.
