# v0.7.0-preview — MCP inbound pillar

Lightweight tactical plan for exposing Vais.Agents as MCP servers — the deferred "inbound MCP" item from the v0.4 research doc §7. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §6.1 (MCP interop) and the post-v0.6 backlog bullet in [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7. Created 2026-04-19.

---

## Scope

**MVP boundary locked 2026-04-19** from the semantic-discussion walkthrough:

1. **Agent = one MCP tool.** Each agent registered in the server's `IAgentRegistry` surfaces as exactly one MCP tool. Name = `agent.id`, description = `agent.description` + version, input schema = `{ "text": string (required), "sessionId"?: string }`, output = agent reply string. Each tool call runs a full `AskAsync` turn against the in-process `IAgentLifecycleManager`. This is the only semantic projection that keeps the word "agent" honest — Options 2/3/4 from the discussion are either misnamed ("toolbox as MCP", "prompt library as MCP") or speculative bloat.
2. **Session handling via optional `sessionId` input.** When supplied, history is preserved via `IAgentSession` scoped to `(agentId, sessionId)`; when absent, runs fresh. Lifecycle is the caller's problem — matches OpenAI Assistants threads.
3. **Streaming tool results from day one.** MCP 2025-06-18 supports chunked tool responses; wire it up in PR 1 against `StatefulAiAgent.StreamAsync` so shipping non-streaming first and retrofitting later doesn't force a wire break.
4. **Two transports**, mirror the outbound package: **stdio** (for Claude Desktop) and **streamableHttp** (for web + composition). SSE deferred — the MCP SDK deprecated it in 2025-03-26 in favour of streamableHttp.
5. **Interrupts surface as structured MCP tool errors.** When a guardrail raises `AgentInterruptedException` mid-call, the server returns `isError: true` with a content block describing the interrupt payload; the caller builds a follow-up tool call carrying the continuation decision in `arguments` that maps to `ResumeAsync`. MCP `elicitation/create` deferred — newer-spec feature that not every client supports yet.
6. **Manifest resource**: the agent's `AgentManifest` is surfaced as an MCP resource (`agent://<id>/manifest`). Read-only discovery for MCP clients that want to see an agent's shape before invoking.
7. **Inbound auth**: JWT bearer over streamableHttp, reusing the v0.6 `IPrincipalMapper` + `AddAgentControlPlaneJwtAuth` wiring. stdio deployments run inside the client's trust boundary (Claude Desktop spawns the process); no inbound auth needed there.
8. **Explicitly deferred to post-v0.7**:
   - MCP **prompts** primitive (publishing agent system prompts / templates as MCP prompts) — cleaner if we ever ship a dedicated "prompt library" shape.
   - MCP **resources** beyond the manifest (session history as MCP resource, audit log as MCP resource) — niche; add when asked.
   - `elicitation/create` for interrupts (requires newer MCP clients than we can assume).
   - `sampling/create` (agent-as-sampler where the MCP server asks the client's LLM to sample) — orthogonal to "agent as server".
   - Multi-agent dispatch into a single MCP server shared by a cluster of agents with fan-out semantics.
   - **Our own MCP Hub** — deferred pillar in its own right (see research doc §7 backlog). Explicitly *complementary* to ContextForge, not a replacement. ContextForge is a protocol-relay: it federates MCP/A2A/REST backends agnostically. Our hub, when it lands, will be **agent-aware** internally (routes through `AgentLifecycleManager`, knows about `AgentManifest` versions, projects registry per-tenant, aggregates `AuditLogEntry` across agents, enforces `IAgentPolicyEngine` at the hub seam) while still speaking MCP on the wire. ContextForge users who want a single gateway keep using ContextForge; consumers who want agent-native routing + policy + audit aggregation get our hub. Both coexist.

### Semantic projection chosen

Option **1** from the design discussion: agent ↔ single MCP tool + optional `sessionId` for continuity. Justification already in the discussion and the Scope points above. Recorded here so future readers know we deliberately *didn't* ship Options 2/3/4.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Session state | Optional `sessionId` in tool input; stateless when absent. Keyed on `(agentId, sessionId)` — ids from different callers or different agents never collide. | Matches OpenAI `Agent.as_tool()` default (fresh session, opt-in continuity); per-agent scoping keeps virtual-server composition safe when multiple agents surface through one MCP endpoint. |
| 2 | Streaming | Ship from PR 1 using MCP streamable tool results + `StreamAsync` | Retrofit would force a wire break; cheap to ship now |
| 3 | Interrupt surfacing | Structured `isError: true` response with continuation payload | Works on every MCP client; caller re-invokes with resume args |
| 4 | Manifest exposure | `agent://<id>/manifest` as an MCP resource | Standard discovery path; resource primitive is one line of wiring |
| 5 | Inbound auth | JWT bearer on streamableHttp; accept both `Authorization` and `X-Upstream-Authorization` headers; reuse v0.6 `IPrincipalMapper` | Zero new auth surface; stdio is in-process trust. The upstream-auth header is the standard MCP-gateway / ContextForge forwarding convention — accepting both means we work both directly and behind a hub. |
| 6 | Transport parity | stdio + streamableHttp; mirror outbound package | SSE deprecated in MCP 2025-03-26 |
| 7 | Tool description format | Multi-line structured text: line 1 = "{id} (v{version}) — {description}"; following lines enumerate budget caps, handoff targets, and a one-line input example. | Gateways like ContextForge surface the description prominently in their admin UI; richer descriptions help discovery when many agents federate through a single virtual server. Resolved upfront — no longer an open question. |

---

## New package

**`Vais.Agents.Protocols.Mcp.Server`** (new) — paired with the shipped `Vais.Agents.Protocols.Mcp` (outbound). Depends on `ModelContextProtocol.Core 1.2.0` (already on CPM); streamableHttp transport pulls ASP.NET Core via `FrameworkReference` the same way `Vais.Agents.Control.Http.Server` does.

One new package only. v0.7 brings total from 19 → **20**.

---

## Delivery

### PR 1 — `McpAgentServer` core + stdio transport

**Packages**: `Vais.Agents.Protocols.Mcp.Server` (new).

Tasks:

- [ ] `McpAgentServer` — wraps an `IAgentRegistry` + `IAgentLifecycleManager`. Implements the MCP server contracts from the SDK (`IMcpServer` or equivalent shape once checked). On `list_tools`: enumerates the registry and emits one MCP tool descriptor per agent with `name = manifest.Id`, `description = $"{manifest.Description ?? manifest.Id} (v{manifest.Version})"`, input schema pinned to `{text, sessionId?}` JSON Schema. On `call_tool`: resolves the agent handle, calls `manager.InvokeAsync` with `AgentInvocationRequest(text, sessionId)`, returns the reply as a single text content block.
- [ ] `AgentSessionResolver` — helper that maps optional `sessionId` tool-arg to the agent's `IAgentSession`. When absent, returns a fresh in-process session; when supplied, tries `IAgentRuntime.GetSession(agentId, sessionId)` (the method shipped in v0.4 session pillar).
- [ ] Streaming path — when the agent's provider implements `IStreamingCompletionProvider`, the server emits a streamed tool result (chunked text content blocks). Otherwise falls back to single-block response.
- [ ] `AddMcpAgentServer()` DI extension — registers `McpAgentServer`. Consumers compose it with the same `IAgentRegistry`/`IAgentLifecycleManager` they use for the HTTP control plane.
- [ ] `StdioAgentServerHost` — `IHostedService` that plumbs stdin/stdout through the MCP SDK's stdio transport. For Claude Desktop + CLI scenarios.
- [ ] Interrupt mapping — catch `AgentInterruptedException` from `InvokeAsync`, return `isError: true` with a content block that embeds the interrupt's `InterruptId` + `Reason` + `Payload` as a JSON text body. Document the continuation shape (caller sends a follow-up `call_tool` with `arguments.resume = { interruptId, payload }` — the server routes this through `ResumeAsync`).
- [ ] Resume arg handling — when tool arguments carry `resume: { interruptId, payload }`, the server calls `ResumeAsync(ResumeInput { InterruptId, Payload, RunId })`. Requires the server to stash `(runId ↔ sessionId)` correlation from the prior interrupt; document this.
- [ ] Tests — 12 new (`Vais.Agents.Protocols.Mcp.Server.Tests`): list_tools enumerates registry, call_tool happy path, stateless vs session-bound calls produce different history lengths, streaming round-trip, interrupt → structured error response, resume arg continues the run, unknown agent → error, unknown session → fresh session created, manifest v-bump emits two tools, schema validation rejects missing `text`, stdio host start/stop round-trip.
- [ ] `PublicAPI.Shipped.txt` empty + `PublicAPI.Unshipped.txt` populated.

### PR 2 — streamableHttp transport

**Packages**: `Vais.Agents.Protocols.Mcp.Server` (extend).

Tasks:

- [x] `MapMcpAgentServer("/mcp")` endpoint extension — mounts the streamableHttp transport on an ASP.NET Core `WebApplication`. Framework-references `Microsoft.AspNetCore.App` like the control-plane server.
- [x] `AddMcpAgentServerJwtAuth(configure)` helper — registers JWT bearer with dual-header event hook. (Named `JwtAuth` vs the plan's `Auth` for symmetry with the v0.6 `AddAgentControlPlaneJwtAuth` helper, and to leave room for non-JWT auth helpers later.) Consumers who don't want auth skip it; stdio host always runs un-auth'd.
- [x] **Dual auth-header support.** `OnMessageReceived` event inspects `X-Upstream-Authorization` alongside `Authorization`; upstream header wins when both present. Test coverage for each path.
- [ ] *Deferred to PR 3:* Correlation across interrupt → resume across separate HTTP calls. The SDK session abstraction is in place but the interrupt-replay path is exercised end-to-end only in PR 3's manifest + resume round-trip test.
- [x] Tests — 6 new HTTP integration tests via ASP.NET Core `TestHost`: route mount (non-404), DI registration of `IOptions<McpServerOptions>` with our handlers, anonymous 401, `Authorization` accepted, `X-Upstream-Authorization` accepted, precedence when both are present.

### PR 3 — manifest resource + polish

**Packages**: `Vais.Agents.Protocols.Mcp.Server` (extend).

Tasks:

- [x] `list_resources` emits `agent://<id>/<version>/manifest` per registered agent. Version baked into the path segment (not a query param) so multi-version discovery is structural, not convention-based.
- [x] `read_resource` returns the manifest as the v0.6 control-plane envelope JSON (`{apiVersion, kind, metadata, spec}`) — same wire shape as the HTTP surface. YAML variant rejected: single source of truth > rendering convenience; clients can pretty-print JSON.
- [x] Tool description refinement per design-question #7 — already landed in PR 1 (`BuildToolDescription`). Covered by the existing `Tool_Description_Includes_Version_Budget_Handoffs_And_Input_Example` test; no PR-3 code change needed.
- [x] Tests — 4 new: list_resources emits uri per agent; read_resource returns envelope JSON; multi-version agent emits distinct resources (and read disambiguates by version); unknown-scheme URIs reject.

### PR 4 — v0.7.0-preview cut

**Packages**: all 20.

Tasks:

- [x] API freeze: `Unshipped` → `Shipped` across the new package. Only `Protocols.Mcp.Server` touched — other 19 packages unchanged since `v0.6.0-preview`.
- [x] Pack: `dotnet pack -c Release -p:VersionPrefix=0.7.0 -p:VersionSuffix=preview -o artifacts/packages` → 20 `.nupkg` + 20 `.snupkg` in local feed.
- [x] Smoketest: extended with an MCP-server probe segment — registers one agent, calls `McpAgentServerBuilder.Build(...)`, asserts all four handlers wired (list-tools/call-tool/list-resources/read-resource) and both capabilities declared. Transport round-trip deferred to the test suite (`RequestContext<T>` isn't directly constructible from outside the SDK — the builder tests use `InternalsVisibleTo` on the static helpers, which the smoketest can't reach).
- [x] Tag: annotated `v0.7.0-preview` on OSS repo `main`. Not pushed.
- [x] Milestone log entry appended to `actor-agents-oss-milestone-log.md`.
- [x] Research doc §7 entry struck — "MCP inbound" line rewritten to point at this pillar.

---

## Exit criteria

- [ ] All 4 PRs on OSS repo `main` (not pushed).
- [ ] 1 new package (`Vais.Agents.Protocols.Mcp.Server`) packs cleanly at `0.7.0-preview`.
- [ ] Full non-container test suite green (expected: ~440+).
- [ ] Smoketest round-trips an agent through MCP end-to-end (outbound client → inbound server wrapping the same agent). This is the acceptance test — if Claude Desktop could replace the outbound client in the smoketest, nothing else changes.
- [ ] `v0.7.0-preview` tag created.
- [ ] Manifest discoverable via MCP `read_resource`; tool descriptor human-readable in any MCP-capable UI.

---

## Decisions locked (from the semantic discussion 2026-04-19)

- **Option 1 semantic**: agent ↔ single MCP tool per agent id. Options 2/3/4 rejected as either "not about agents" (2, 3) or premature bloat (4).
- **`sessionId` optional, caller-owned lifecycle**. No auto-eviction; no server-side TTL in v0.7.
- **Streaming from day one**, via MCP chunked tool results against `StatefulAiAgent.StreamAsync`.
- **Interrupts as structured errors**, continuation via follow-up `call_tool` carrying a `resume` arg. `elicitation/create` deferred.
- **Manifest as `agent://<id>/manifest` resource**, rendered as JSON (same envelope shape as the control-plane HTTP surface).
- **JWT bearer on streamableHttp**, reusing v0.6's `IPrincipalMapper` + middleware. stdio host runs un-auth'd (in-process trust).
- **SSE transport skipped** — deprecated upstream. stdio + streamableHttp only.
- **No sampling/create** — orthogonal to "agent as server"; explicitly out of scope for v0.7.

### Open questions (low-stakes, resolve during impl)

1. Resume-arg schema stability — is `resume: { interruptId, payload }` the canonical shape, or should it piggyback on the text arg with a convention? Lean dedicated arg for clarity.
2. Multi-version agent behaviour — when the registry has `support v1.0` and `support v1.1`, does the server emit two tools (`support@1.0` / `support@1.1`) or one tool (latest)? Lean latest-by-default with a `version` input arg override for determinism.
3. Tool name sanitisation — MCP tool names have a character restriction; `agent.id` is already restricted to `[a-z0-9-]` per v0.6 manifest validation, so pass-through should work. Verify against the SDK's actual regex.
4. What happens on manifest update mid-call — does an in-flight tool call see the old manifest or the new one? Lean snapshot-at-call-start semantics (matches Orleans grain activation behaviour).

---

## Progress log

- 2026-04-19 — plan created after semantic-discussion walkthrough. Option 1 chosen; six design questions resolved; 4-PR split. **Pending**: start on PR 1 (`McpAgentServer` core + stdio transport) or iterate on the semantic further.
- 2026-04-19 — research pass on OpenAI `Agent.as_tool()` + IBM ContextForge folded in. Three adjustments:
  1. Session-id scoping explicitly documented as `(agentId, sessionId)` — two agents in the same virtual server never collide on caller-supplied ids. Resolved research question #1.
  2. PR 2's JWT middleware accepts both `Authorization` and `X-Upstream-Authorization` headers (ContextForge / gateway-forwarding convention). Upstream header wins when both present.
  3. Tool description format locked as multi-line structured text (was previously an open question). Includes id, version, budget block, handoffs block, input example — discoverable in gateway UIs without reading the manifest resource.
  Plus a forward-looking note: our own future MCP Hub will be agent-aware (routes through `AgentLifecycleManager`, enforces policy, aggregates audit across agents) — explicitly *complementary* to ContextForge's protocol-relay shape, not a replacement. Both coexist.
- 2026-04-19 — **v0.7 PR 1 landed** as `badd6c9` on OSS repo `main` (not pushed). New package `Vais.Agents.Protocols.Mcp.Server`:
  - `McpAgentServerBuilder.Build(registry, lifecycle, options)` returns a configured `McpServerOptions` hooking `ListToolsHandler` (enumerates registry, one MCP tool per agent with locked `{text, sessionId?, resume?}` schema) and `CallToolHandler` (routes to `lifecycle.InvokeAsync`).
  - Structured error envelopes for the three exception paths: `AgentInterruptedException` → `{interruptId, reason, runId, agentId, continuation}`; `AgentPolicyDeniedException` → `{code: "policy-denied", operation, reason}`; `AgentBudgetExceededException` → `{code: "budget-exceeded", field}`. All with `IsError: true`.
  - Tool description format (design-question #7 resolved upfront): multi-line `"{id} (v{version}) — {desc}" + budget block + handoffs block + input example`.
  - `resume.interruptId` arg short-circuits `text` and routes a fresh `Invoke` with `resume.*` metadata tags. v0.6 has no resume verb yet; PR 4 of this pillar wires true resume-through-journal.
  - `StdioAgentServerHost` (BackgroundService) + `AddMcpAgentServerStdio(configure?)` DI helper for Claude Desktop / local spawn.
  - Microsoft.Extensions.Hosting.Abstractions 10.0.6 added to CPM.
  - 13 new tests in a new `Vais.Agents.Protocols.Mcp.Server.Tests` project (all via `InternalsVisibleTo` on the static handler methods; no live MCP transport needed).
  - **435 total non-container** (Core 287; new Mcp.Server.Tests 13). 0 warnings.
  - **Deferred to PR 2**: MCP streamable tool responses via `StreamAsync` (non-streaming response in PR 1 — wire shape is forward-compatible since MCP streaming is additive notifications, not a result-shape swap). streamableHttp transport, JWT auth with `X-Upstream-Authorization`.
  - **Deferred to PR 3**: `list_resources` / `read_resource` for `agent://{id}/manifest`.
- 2026-04-19 — **v0.7 PR 3 landed** as `8e94a7c` on OSS repo `main` (not pushed). Manifest resources:
  - `list_resources` emits `agent://{id}/{version}/manifest` per registered agent — one URI per (id, version) pair, so multi-version agents discover as distinct resources.
  - `read_resource` returns the v0.6 control-plane envelope JSON (`{apiVersion, kind, metadata, spec}`). Same wire shape the HTTP control plane speaks — single source of truth for the manifest contract across protocols.
  - Internal `ManifestEnvelopeSerializer` is a copy of `Vais.Agents.Control.Http.Client.EnvelopeSerializer`. Server package shouldn't take a ProjectReference on the client package; the shape is pinned by `AgentManifest`'s record layout and drift is caught by round-trip tests on both sides.
  - `ServerCapabilities.Resources = { ListChanged = true }` declared in both `McpAgentServerBuilder.Build` and `AddMcpAgentServerHttp`, matching the existing `Tools` capability pattern.
  - Tool-description format (design-question #7) verified as already-shipped in PR 1 — `BuildToolDescription` covers id + version + budget block + handoffs block + input example. Existing test kept as-is.
  - 4 new tests, 23 total in Mcp.Server.Tests (13 PR1 + 6 PR2 + 4 PR3). **445 total non-container** (+4). 0 warnings.
  - **Deferred to PR 4**: v0.7.0-preview cut — API freeze, pack, smoketest MCP round-trip, tag.
- 2026-04-19 — **v0.7 PR 2 landed** as `bdfb33d` on OSS repo `main` (not pushed). streamableHttp transport + JWT dual-header auth:
  - `AddMcpAgentServerHttp` wraps the SDK's `AddMcpServer().WithHttpTransport()` and installs our list-tools / call-tool handlers via service-resolved closures — same agent-wrapping semantics as stdio.
  - `AddMcpAgentServerJwtAuth` registers JWT bearer with dual-header support via `OnMessageReceived`: `X-Upstream-Authorization` wins over `Authorization` when both present.
  - `MapMcpAgentServer("/mcp")` delegates to the SDK's `MapMcp` — transport plumbing stays in the SDK, our value-add is the builder + auth wiring.
  - `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` 1.2.0 + `Microsoft.AspNetCore.Authentication.JwtBearer` added; `FrameworkReference Microsoft.AspNetCore.App` on both src + test csproj. `NoWarn IDE0005` to suppress the analyzer's false-positive "unused using" on ASP.NET Core namespaces (matches `Vais.Agents.Control.Http.Server` pattern).
  - `McpAgentServerOptions` relaxed `init` → `set` so the `AddMcpAgentServerHttp(configure: o => { o.Name = …; … })` lambda works.
  - 6 new HTTP integration tests via ASP.NET Core `TestHost`: route mount, DI registration, anonymous 401, `Authorization` accepted, `X-Upstream-Authorization` accepted, precedence when both present. `UpstreamAwareTestAuthHandler` mirrors the middleware's dual-header logic end-to-end so we test the pipeline not just the extension method.
  - **441 total non-container** (was 435, +6). 0 warnings.
  - **Deferred to PR 3**: interrupt→resume round-trip across separate HTTP calls; MCP streamable tool responses via `StreamAsync`; `list_resources` / `read_resource` for `agent://{id}/manifest`; tool description polish per design-question #7.
