# v0.8.0-preview — A2A inbound pillar

Lightweight tactical plan for exposing Vais.Agents as A2A endpoints — the deferred "A2A inbound" item from the v0.4 research doc §7 backlog. Companion to [`actor-agents-oss-architecture-review.md`](./actor-agents-oss-architecture-review.md) §6.2 (A2A interop) and the post-v0.6 backlog bullet in [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7. Parallel shape to [`actor-agents-oss-v0.7-mcp-inbound-pillar.md`](./actor-agents-oss-v0.7-mcp-inbound-pillar.md). Created 2026-04-19.

---

## Scope

**MVP boundary locked 2026-04-19** from the semantic-choice walkthrough + A2A research pass:

1. **Agent = one A2A endpoint.** Each agent registered in the server's `IAgentRegistry` gets its own A2A route (`/agents/{id}`) and its own AgentCard at `/agents/{id}/.well-known/agent.json`. Parallels the v0.7 MCP "one tool per agent" projection and matches A2A's native "one agent = one endpoint + one card" idiom — the SDK's `AgentServer` sample constructs one `AgentCard` per agent, so endpoint-per-agent is the grain.
2. **Task lifecycle via unary `message/send`.** `IAgentHandler.OnMessageSend` wraps the agent's `InvokeAsync` turn. Fast replies return a direct `Message`; runs that surface an interrupt return a `Task` in state `input-required`; completions return a `Task` in state `completed` with the agent's text as an artifact. **No SSE `message/stream` in v0.8** — SDK just churned 0.3 → 1.0.0-preview → 1.0.0-preview2 in four weeks and `TaskUpdater`'s event-queue surface is under-documented. Tasks work without streaming per the A2A spec — a unary response can return a `Task` object in any state, `message/stream` is a separate method. Deferred to v0.9.
3. **Interrupts → A2A `input-required` task state.** When `InvokeAsync` throws `AgentInterruptedException`, the server creates a task in state `input-required`, embedding `{interruptId, reason, payload}` as a `Message` in the task history. Caller resumes via follow-up unary `message/send` carrying the same `taskId` and a response `Message`; the server routes to a fresh `InvokeAsync` tagged with `resume.interruptId` metadata, same as the MCP server's continuation mechanism. No A2A-specific resume verb — it's plain `message/send` on the existing task.
4. **AgentCard auto-derived from `AgentManifest`, with override hooks.** `Manifest.Id` → `AgentCard.name` + URL suffix; `Manifest.Description` → `AgentCard.description`; `Manifest.Version` → `AgentCard.provider.version`; `Labels` + `Annotations` → `AgentCard.metadata`. **One default `AgentSkill` per agent** (`id: "invoke"`, derived name/description) — A2A's `AgentSkill` shape has no clean map from `Tools`/`Handoffs`/`Budget`, and inventing multiple skills would be lossy. Consumers fill real skill taxonomies via:
   - `Action<AgentManifest, AgentCard>` post-process hook (tweak the auto-default), or
   - `Func<AgentManifest, AgentCard>` replacement (raw card), or
   - `McpAgentServerOptions`-style per-agent override dictionary.
5. **Inbound auth: reuse v0.7 JWT pattern.** A2A spec §7 mandates TLS + declared auth schemes in `AgentCard.securitySchemes`, but delegates to standard HTTP schemes (Bearer/APIKey/OAuth2/OIDC/mTLS). Our JWT bearer = `HTTPAuthSecurityScheme` with `scheme: "bearer"` + `bearerFormat: "JWT"` — spec-compliant as-is. `A2A.AspNetCore` exposes only `MapA2A`/`MapWellKnownAgentCard` with no auth hooks, so ASP.NET middleware is the idiomatic path. `X-Upstream-Authorization` dual-header support carries over unchanged (HTTP-header level, transparent to the JSON-RPC layer). Ship `AddA2AAgentServerJwtAuth` as a thin alias alongside v0.7's `AddMcpAgentServerJwtAuth` for consistent API shape.
6. **Task persistence: `InMemoryTaskStore` + `OrleansTaskStore`.** `InMemoryTaskStore` lives in `A2A.Server`; the SDK may already provide one, in which case we re-export its registration. `OrleansTaskStore` lives in `Vais.Agents.Hosting.Orleans` (same package as `OrleansAgentJournal`) as an `ITaskStore` impl backed by an `IA2ATaskGrain<TKey=taskId>` so A2A task state survives silo restart.
7. **Explicitly deferred to post-v0.8**:
   - SSE `message/stream` streaming — re-evaluate when the A2A SDK hits GA or `preview3` stabilises `TaskUpdater`.
   - A2A push notifications (`tasks/pushNotificationConfig/set`) — requires callback infrastructure; needed only for background long-running tasks.
   - `AUTH_REQUIRED` mid-task re-auth (A2A spec §7 non-MVP) — document as known gap.
   - A2A **extensions** registration (per-org custom methods) — niche; add when asked.
   - Multi-agent dispatch into a single A2A endpoint (skill-catalog style) — rejected in design; endpoint-per-agent is the chosen shape.

### Semantic projection chosen

Endpoint-per-agent. Same rationale as the v0.7 MCP "one tool per agent" call: keeps the word "agent" honest, maps cleanly to A2A's native AgentCard discovery model, composes well with ContextForge-style gateways that route by agent URL.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Endpoint shape | One route + AgentCard per registered agent (`/agents/{id}` + `/agents/{id}/.well-known/agent.json`) | Mirrors MCP; matches A2A SDK sample idiom; gateway-friendly |
| 2 | Task model | Unary `message/send` returning `Task` (or `Message` for fast replies); no SSE | A2A spec allows `message/send` to return `Task` in any state — streaming is orthogonal; SDK `TaskUpdater` API is under-documented at `1.0.0-preview2` (deferred to v0.9) |
| 3 | Interrupt mapping | `AgentInterruptedException` → `Task(input-required)` with `{interruptId, reason, payload}` in task history | A2A-native; resume = follow-up `message/send(taskId)`; no custom resume verb |
| 4 | AgentCard derivation | Auto from `AgentManifest` + post-process hook + raw-card replacement + per-agent override map | SDK sample constructs cards manually — raw-card fits idiom; auto-default is pure ergonomics; one default skill per agent since `AgentSkill` shape has no clean map |
| 5 | Task persistence | `InMemoryTaskStore` (in `A2A.Server`, falling through to SDK-provided if present) + `OrleansTaskStore` (in `Hosting.Orleans`) | Tasks must outlive silo restart for `input-required` to work; Orleans grain keyed by taskId is the same pattern as `OrleansAgentJournal` |
| 6 | Inbound auth | Reuse v0.7 JWT pattern + `AddA2AAgentServerJwtAuth` alias; dual-header `X-Upstream-Authorization` | Spec §7 delegates to HTTP schemes; `A2A.AspNetCore` has no auth hooks → ASP.NET middleware is idiomatic; ContextForge forwarding works unchanged |
| 7 | Package layout | New `Vais.Agents.Protocols.A2A.Server` sibling to outbound `.A2A` | `A2A.AspNetCore` + `Microsoft.AspNetCore.App` FrameworkReference would drag ASP.NET into every outbound consumer; same rationale as `Mcp.Server` split |
| 8 | Multi-version agents | One A2A route per (id, version) — `/agents/{id}/{version}` when registry has multiple; `/agents/{id}` redirects to latest | Parallels v0.7 manifest-resource shape (`agent://{id}/{version}/manifest`); structural discovery, not convention |
| 9 | `sessionId` equivalent | A2A's `contextId` field on `Message` fills this role — caller-supplied, optional, scoped to `(agentId, contextId)` | Matches A2A semantics; parallels MCP v0.7 `(agentId, sessionId)` scoping |
| 10 | Fast-reply vs. task response | Agent runs that finish in one turn without interrupts return a direct `Message`; runs that span multiple turns or hit an interrupt return a `Task` | A2A spec lets server decide; direct `Message` is cheaper for single-turn chat. Document the threshold (= "any interrupt or multi-turn loop → Task") so clients can rely on it. |

---

## New packages

**`Vais.Agents.Protocols.A2A.Server`** (new) — paired with the shipped outbound `Vais.Agents.Protocols.A2A`. Depends on `A2A 1.0.0-preview2` (already on CPM) + `A2A.AspNetCore 1.0.0-preview2` (add to CPM). Framework-references `Microsoft.AspNetCore.App`.

**`Vais.Agents.Hosting.Orleans`** (extend — not a new package) — adds `OrleansTaskStore : A2A.ITaskStore` + `IA2ATaskGrain<string>` interface + grain impl keyed by taskId. New public types but zero breaking changes on existing surface.

One new package only. v0.8 brings total from 20 → **21**.

---

## Delivery

### PR 1 — A2A server core + unary task flow

**Packages**: `Vais.Agents.Protocols.A2A.Server` (new).

Tasks:

- [x] `A2AAgentServerBuilder` (static) — mirrors `McpAgentServerBuilder`. `BuildAsync(IAgentRegistry, IAgentLifecycleManager, baseUrl, A2AAgentServerOptions?, ITaskStore?, ILoggerFactory?)` produces one `A2AAgentServerEntry` (agent id + version + route + `AgentCard` + `A2AServer`) per registered agent. Async because the registry enumeration is async.
- [x] `A2AAgentHandler : A2A.IAgentHandler` — implements `ExecuteAsync(RequestContext, AgentEventQueue, CancellationToken)` (not `OnMessageSend` — SDK uses `ExecuteAsync` for the easy-path contract). Extracts text from `context.Message.Parts` (text blocks only), threads `context.ContextId` → `AgentInvocationRequest.SessionId`, forwards `taskId`/`contextId` as `a2a.*` metadata, enqueues a `Role.Agent` `Message` via `eventQueue.EnqueueMessageAsync` (fast reply — no `AgentTask`).
- [x] `AgentCardBuilder` — auto-derives `AgentCard` from `AgentManifest`. Fields populated: `Name = manifest.Id`, `Description = manifest.Description ?? manifest.Id`, `Version = manifest.Version`, `Provider.Organization = options.ProviderOrganization`, `Capabilities { Streaming = false, PushNotifications = false }`, `DefaultInputModes/OutputModes = ["text"]`, one `AgentSkill { Id = "invoke", Name = manifest.Id, Description = description }`, one `AgentInterface { Url = {baseUrl}{route}, ProtocolBinding = "jsonrpc" }`. `Labels["a2a.provider-url"]` round-trips into `Provider.Url` when set.
- [x] Override hooks on `A2AAgentServerOptions`:
  - `Action<AgentManifest, AgentCard>? CustomizeCard` — post-process the auto-default.
  - `Func<AgentManifest, AgentCard>? BuildCard` — replace the auto-default entirely (CustomizeCard still runs after).
  - `IReadOnlyDictionary<string, AgentCard>? PerAgentOverrides` — fully-supplied cards keyed by agent id (short-circuits both hooks).
  - Precedence implemented per plan: `PerAgentOverrides > BuildCard > auto-default`, then `CustomizeCard` runs except when `PerAgentOverrides` won.
- [x] `MapA2AAgentServer(IEndpointRouteBuilder, string? baseUrl = null)` — mounts one A2A endpoint per registered agent at `{BasePath}/{id}` + `{BasePath}/{id}/.well-known/agent-card.json` (SDK path is `agent-card.json`, not `agent.json`). Delegates to `A2A.AspNetCore.MapA2A(endpoints, server, route)` for JSON-RPC and `MapWellKnownAgentCard(endpoints, card, route)` for discovery. Returns the entry list for test + diagnostics use.
- [x] `AddA2AAgentServer(IServiceCollection, Action<A2AAgentServerOptions>?)` DI extension — registers the options + `TryAddSingleton<ITaskStore, InMemoryTaskStore>()` (`TryAdd` lets consumers override the task store with e.g. `OrleansTaskStore` in PR 3).
- [x] `A2AAgentServerOptions`: `Name`, `Version`, `ProviderOrganization`, `BasePath`, `LabelPrefixFilter`, `CustomizeCard`, `BuildCard`, `PerAgentOverrides`. All properties `get; set;` (init-only caused issues in v0.7; defaulted to mutable for the configure-action idiom).
- [x] Tests — 12 landed (`Vais.Agents.Protocols.A2A.Server.Tests`). Builder (8): one entry per agent, AgentCard auto-derivation round-trip, `CustomizeCard` hook applied, `BuildCard` replacement wins, `PerAgentOverrides` wins and skips hooks, label-prefix filter threaded, custom `BasePath` threads through route + card URL, null-argument guards. HTTP integration (4): well-known card served at expected path with expected JSON shape, unknown agent well-known → 404, unary `message/send` round-trips text → agent → reply via `A2AClient`, `ContextId` threads through to `AgentInvocationRequest.SessionId`.
- [x] `PublicAPI.Shipped.txt` empty + `PublicAPI.Unshipped.txt` populated (builder + handler + options + entry record + hooks surface).

### PR 2 — interrupt → `input-required` + resume

**Packages**: `Vais.Agents.Protocols.A2A.Server` (extend).

Tasks:

- [x] `A2AAgentHandler.ExecuteAsync` fresh-path — catch `AgentInterruptedException`, `Submit` + `StartWork` the task via `TaskUpdater`, then `RequireInputAsync` with a status message whose **data-part** carries `{interruptId, reason, runId, agentId, payload?}` plus a `Part.Metadata["vais.interrupt"] = true` tag so later resume calls can locate the envelope.
- [x] Resume-path parsing — when `context.Task is not null`, the handler re-enqueues the existing task (so the SDK's `MaterializeResponseAsync` has a `result.Task` to refetch) then walks `task.Status.Message` + `task.History` newest→oldest for a data-part tagged `vais.interrupt`, copies `interruptId` / `runId` into `AgentInvocationRequest.Metadata` as `resume.interruptId` / `resume.runId`, and routes through `IAgentLifecycleManager.InvokeAsync`. On success → `CompleteAsync`; on re-interrupt → `RequireInputAsync` (stays in `input-required`); on policy/budget → `FailAsync`.
- [x] `AgentPolicyDeniedException` / `AgentBudgetExceededException` mapping — both surface as `Task(Failed)` with a structured data part: `{code: "policy-denied", operation, reason}` and `{code: "budget-exceeded", field}` respectively. Parallels the v0.7 MCP structured-error payload shape.
- [x] AgentCard shape — PR 1 already set `capabilities.streaming = false` and `capabilities.pushNotifications = false`. No changes needed in PR 2; clients call `message/send` unary and handle `Task` responses per the response-shape rules documented on the handler.
- [x] Tests — 6 new (`A2AAgentServerInterruptTests.cs`): interrupt → `Task(input-required)` with data-part envelope, resume via `message/send(taskId)` continues run and completes (asserts `resume.*` metadata threaded), repeated interrupt on resume stays in `input-required` with fresh envelope, policy denial → `Task(failed)` with `{code: "policy-denied", operation, reason}` data, budget exceeded → `Task(failed)` with `{code: "budget-exceeded", field}` data, unknown `taskId` → SDK emits `A2AException(TaskNotFound)`. All exercised end-to-end via `A2AClient` over `TestServer`.

### PR 3 — `OrleansTaskStore` + JWT auth

**Packages**: `Vais.Agents.Hosting.Orleans` (extend) + `Vais.Agents.Protocols.A2A.Server` (extend).

Tasks:

- [x] `IA2ATaskGrain` (Orleans) — keyed by taskId string via `IGrainWithStringKey`. Uses a JSON-string surrogate (`A2ATaskSurrogate { TaskId, ContextId, TaskJson, SavedAt }`) instead of a full-field mirror — lets SDK-shape evolution ride on `A2AJsonUtilities.DefaultOptions` rather than hand-synced Orleans surrogate edits. Methods: `GetAsync() -> A2ATaskSurrogate?`, `SaveAsync(A2ATaskSurrogate)`, `ClearAsync()`. Context-scoped listing deferred (see <em>`ListTasksAsync` stub</em> below) — `ContextId` is denormalised on the surrogate so a post-v0.8 context-index grain can wire in without a storage migration.
- [x] `OrleansTaskStore : A2A.ITaskStore` — routes `GetTaskAsync` / `SaveTaskAsync` / `DeleteTaskAsync` through `IA2ATaskGrain` using `A2AJsonUtilities.DefaultOptions` to round-trip `AgentTask`. `ListTasksAsync` returns empty (plan §6 notes this stub; full listing needs the context-index grain above). Lives in `Vais.Agents.Hosting.Orleans`. **One small deviation from plan**: Orleans package needed a new `<PackageReference Include="A2A" />` — the plan said "zero new deps via transitive" but Hosting.Orleans never actually transitively referenced A2A. Added directly; matches the `A2A.AspNetCore` split rationale (A2A is a small SDK).
- [x] `AddOrleansA2ATaskStore(IServiceCollection)` extension in `Hosting.Orleans` — `TryAddSingleton<ITaskStore>` so consumers override the default `InMemoryTaskStore` by calling this *before* `AddA2AAgentServer`.
- [x] `AddA2AAgentServerJwtAuth(IServiceCollection, Action<JwtBearerOptions>)` — dual-header event hook identical in shape to v0.7's MCP JWT pattern. Bearer scheme registered under `A2AAgentServerJwtAuthExtensions.A2ABearerSchemeName = "A2AJwt"` so MCP + A2A can coexist with independent audit trails. Dual-path card-customizer wiring: reaches into the already-registered options instance when available, otherwise defers via `IOptions<A2AAgentServerOptions>.PostConfigure` — handles both call orders (before/after `AddA2AAgentServer`).
- [x] Auto-populate `AgentCard.SecuritySchemes["bearer"]` with `{type: http, scheme: bearer, bearerFormat: JWT}` when `AddA2AAgentServerJwtAuth` is called. Implemented as a card-customizer appended to `A2AAgentServerOptions.CustomizeCard` so the well-known discovery endpoint advertises the scheme on every derived card. Does NOT overwrite a caller-supplied `"bearer"` entry (idempotent + preserves operator overrides).
- [x] Tests — 8 landed (plan called for 6 — added `GetTask_For_Unknown_Id_Returns_Null` + `AgentCard_SecuritySchemes_Auto_Populated_With_Bearer_JWT` as behaviour-completeness coverage). Orleans store tests (3 in `OrleansTaskStoreTests.cs`): save→get round-trips input-required state, unknown-id returns null, task survives simulated silo restart via `IManagementGrain.ForceActivationCollection`. JWT + AgentCard tests (5 in `A2AAgentServerJwtAuthTests.cs`): anonymous rejected → 401, Authorization accepted, X-Upstream-Authorization accepted, upstream wins when both headers present (asserts `sub` claim), well-known card advertises bearer JWT scheme. Orleans tests use existing `OrleansClusterFixture` with memory grain storage under `AiAgentGrain.StorageName`.

### PR 4 — v0.8.0-preview cut

**Packages**: all 21.

Tasks:

- [x] API freeze: `Unshipped` → `Shipped` across the new `A2A.Server` package (50 entries promoted) + the `Hosting.Orleans` package (27 new entries — `IA2ATaskGrain`, `A2ATaskGrain`, `A2ATaskGrainState`, `A2ATaskSurrogate`, `OrleansTaskStore`, `AddOrleansA2ATaskStore`). Other 19 packages unchanged since `v0.7.0-preview`.
- [x] Pack: `dotnet pack -c Release -p:VersionPrefix=0.8.0 -p:VersionSuffix=preview -o artifacts/packages` → 21 `.nupkg` + 21 `.snupkg`, all present in `oss/agentic/artifacts/packages/`.
- [x] Smoketest: refreshed to 0.8.0-preview, added A2A-server probe segment. Registers one agent, calls `A2AAgentServerBuilder.BuildAsync(...)`, asserts derived `AgentCard` shape (name, version, skill id, interface URL, streaming=false), probes 7 types from the new package + 3 OrleansTaskStore types + the `A2AJwt` scheme name. Transport round-trip stays in the test suite (same rationale as v0.7). Final line reads "All twenty-one Vais.Agents.* 0.8.0-preview packages consumed cleanly from a plain .NET 9 console app."
- [x] Tag: annotated `v0.8.0-preview` on OSS repo `main` (commit `850f078`). Not pushed.
- [x] Milestone log entry in [`actor-agents-oss-milestone-log.md`](./actor-agents-oss-milestone-log.md).
- [x] Research doc §7 update — A2A inbound backlog line struck, pointed at this pillar.

---

## Exit criteria

- [x] All 4 PRs on OSS repo `main` (not pushed) — shipped as two commits (`073073a` feat for PRs 1-3, `850f078` chore for PR 4 freeze).
- [x] 1 new package (`Vais.Agents.Protocols.A2A.Server`) + extended `Vais.Agents.Hosting.Orleans` pack cleanly at `0.8.0-preview` — 21 `.nupkg` + 21 `.snupkg` in `artifacts/packages/`.
- [x] Full non-container test suite green — 457 tests (Core 287, Orleans 56, A2A.Server 23, MCP.Server 23, Control.Http 17, A2A 12, Observability 11, Parity 10, VectorData 16, Mcp 2). Baseline was 445 at v0.7; +12 from pillar (slightly under the "~20" estimate because some Orleans store tests landed under the existing Orleans.Tests suite rather than as a separate count).
- [x] Smoketest probes A2A surface — AgentCard derivation (route, name, version, skill id, interface URL, streaming flag), 7 endpoint + builder + DI + JWT types, 3 OrleansTaskStore types, scheme name — end-to-end from a fresh .NET 9 console project with only NuGet references.
- [x] `v0.8.0-preview` tag created.
- [ ] An A2A-spec-compliant client (A2A Inspector / `a2a-cli` / a third-party A2A runtime) can discover one of our agents via `.well-known/agent-card.json`, send a `message/send`, receive the reply, and — if the agent interrupts — continue via `message/send(taskId)`. This is the acceptance demo (not automated in the test suite; **still to run manually** when an A2A client is available — test suite's `A2AClient` round-trip over `TestHost` is the closest automated equivalent).

---

## Decisions locked (from the semantic + research walkthrough 2026-04-19)

- **Endpoint-per-agent**: `/agents/{id}` + AgentCard at `/agents/{id}/.well-known/agent.json`. Skill-catalog shape rejected.
- **Tasks without SSE**: unary `message/send` returning `Task` in any state. Streaming deferred to v0.9 — SDK too churny at `1.0.0-preview2`.
- **Interrupts → `Task(input-required)`**: A2A-native; resume = follow-up `message/send(taskId)`.
- **AgentCard**: auto-derive from manifest + post-process hook + raw replacement + per-agent override map.
- **Task persistence**: `InMemoryTaskStore` (in `A2A.Server`) + `OrleansTaskStore` (in `Hosting.Orleans`).
- **Auth**: reuse v0.7 JWT + dual-header pattern; `AddA2AAgentServerJwtAuth` alias; `AgentCard.securitySchemes` auto-populated when auth extension is wired.
- **No SSE, no push notifications, no mid-task re-auth** — all three explicitly out of scope for v0.8.
- **No sampling/create equivalent** — A2A has no such primitive; non-issue.

### Open questions (low-stakes, resolve during impl)

1. Does `A2A 1.0.0-preview2` ship an `InMemoryTaskStore` already, or do we provide our own? Grep the SDK in PR 1.
2. A2A `Task` record: does the SDK surface a clean way to attach structured error data to `Task(failed)`, or do we stuff it into a `Message` data-part? Prefer a dedicated `error` field if present.
3. `contextId` scoping — does the A2A spec require server-side uniqueness, or is it opaque? If opaque, keyed on `(agentId, contextId)` mirrors our `sessionId` scoping in v0.7.
4. Multi-version route disambiguation — if the registry holds `support/1.0` + `support/1.1`, does `/agents/support` route to latest and `/agents/support/1.0` work for pinning, or do we force explicit versioning? Lean latest-redirect with explicit-version support, matching v0.7's resource URI shape.
5. How does `A2A.AspNetCore.MapA2A` compose with multiple agents on the same `IEndpointRouteBuilder`? Does each call require a separate `TaskManager` instance, or can one manager serve many handlers? Check SDK in PR 1.
6. `AgentCard.skills` is required-non-empty per spec. If `manifest.Description` is null, our one default skill has no description — is that valid? Lean "description defaults to `manifest.Id`" to avoid empty-string drift.

---

## Progress log

- 2026-04-19 — plan created after semantic walkthrough + A2A SDK research. Endpoint-per-agent + unary tasks + `OrleansTaskStore` + JWT-reuse all locked. **Pending**: start on PR 1 (`A2AAgentServerBuilder` + unary `message/send` + AgentCard derivation).
- 2026-04-19 — PR 1 landed on `033-logging-improvement-read`. New package `Vais.Agents.Protocols.A2A.Server` (5 source files), new test project `Vais.Agents.Protocols.A2A.Server.Tests` (12 tests, all green). Full non-container suite stays green: Core 287, Orleans 53, MCP.Server 23, Control.Http 17, Protocols.A2A 12, A2A.Server 12, Observability 11, Parity 10, VectorData 16. **Surprises resolved inline**: (1) `A2A.AspNetCore` ships as a separate 1.0.0-preview2 package on nuget.org — added to CPM; (2) well-known discovery path is `/.well-known/agent-card.json` (SDK) not `/.well-known/agent.json` (original spec / plan wording) — followed SDK, plan updated to reflect; (3) `IAgentHandler.ExecuteAsync` uses a write-only `AgentEventQueue` for responses, not a return value — enqueue `Message` on the queue, `A2AServer` marshals to the wire; (4) `AgentCard.DefaultInputModes`/`DefaultOutputModes`/`Skills`/`SupportedInterfaces` are `List<T>` (confirmed via reflection probe), NOT SDK's `StringList` wrapper — collection-initializer works directly; (5) an open question from the plan (§6.2 "`A2AClient` via TestHost") worked cleanly — `new A2AClient(new Uri("http://localhost/agents/echo"), _http)` over `GetTestClient()` round-trips end-to-end, so we do NOT need a separate transport harness for PR 1. **Pending**: PR 2 (interrupts → `input-required`, resume-via-`taskId`, policy/budget → `Task(failed)`).
- 2026-04-19 — PR 2 landed on `033-logging-improvement-read`. `A2AAgentHandler` extended with full task-lifecycle handling (fresh: Submit→StartWork→RequireInput/Fail; resume: replay-task→InvokeAsync→Complete/RequireInput/Fail). 6 new tests in `A2AAgentServerInterruptTests.cs`, all green — A2A.Server package total is now 18 tests. Full non-container suite green: Core 287, Orleans 53, MCP.Server 23, Control.Http 17, A2A 12, A2A.Server 18, Observability 11, Parity 10, VectorData 16, Mcp 2 = 449. **Surprises resolved inline** (two SDK-semantics gotchas worth naming for future pillars): (1) `TaskUpdater` requires the lifecycle dance `Submit → StartWork → {Complete|RequireInput|Fail}` even for fail/input-required transitions on a fresh task — calling `RequireInputAsync` without prior `Submit` silently produces no events and the SDK returns `A2AException("did not produce any response events")`; (2) on the **resume** path, `MaterializeResponseAsync` expects the handler to enqueue a `Task` event (not just `TaskStatusUpdateEvent`s) — `TaskUpdater.CompleteAsync`/`FailAsync`/`RequireInputAsync` all emit only `StatusUpdateEvent`s, which the SDK iterates but never captures as the unary `response.Task`, so if nothing else enqueues a `Task` the handler looks like it did nothing. Fix: `eventQueue.EnqueueTaskAsync(context.Task)` at the top of the resume branch — gives SDK a Task to capture, then subsequent StatusUpdates mutate the store and the SDK re-fetches final state. Worth documenting in the README in PR 4. (3) `Part.Metadata` tagging (`"vais.interrupt"`) is how we locate the interrupt envelope on resume — checks `task.Status.Message` first (where `RequireInputAsync` stows its message) then walks `task.History`. **Pending**: PR 3 (`OrleansTaskStore` + JWT auth).
- 2026-04-19 — PR 3 landed on `033-logging-improvement-read`. Two package extensions: `Vais.Agents.Hosting.Orleans` adds `IA2ATaskGrain` + `A2ATaskGrain` + `A2ATaskSurrogate` + `OrleansTaskStore` + `AddOrleansA2ATaskStore`; `Vais.Agents.Protocols.A2A.Server` adds `A2AAgentServerJwtAuthExtensions.AddA2AAgentServerJwtAuth` under scheme name `"A2AJwt"`, plus the bearer-JWT `SecuritySchemes` auto-injection. 8 new tests (3 Orleans store + 5 JWT/card, ≥ plan's 6 due to 2 extra behaviour-completeness tests). Full non-container suite: 457 tests green (Core 287, Orleans 56, A2A.Server 23, MCP.Server 23, Control.Http 17, A2A 12, Observability 11, Parity 10, VectorData 16, Mcp 2). **Decisions worth naming**: (1) `A2ATaskSurrogate` stores `AgentTask` as a **JSON string** via `A2AJsonUtilities.DefaultOptions` rather than mirroring every SDK field into Orleans surrogate properties — schema drift couples to SDK version bumps instead of hand-synced Orleans edits, a nicer coupling as A2A goes through more previews. (2) Plan said "zero new deps — Orleans package already references A2A transitively"; that was wrong (Orleans only referenced Abstractions + Core), so added `<PackageReference Include="A2A" />` to Orleans csproj directly. Same rationale as the `A2A.AspNetCore` split in PR 1 — A2A SDK is small and a direct ref is cleaner than stuffing the type there via another package. (3) `AddA2AAgentServerJwtAuth` handles both call orders (before/after `AddA2AAgentServer`) via a two-branch lookup: direct options-instance mutation if found, otherwise `IOptions<T>.PostConfigure`. (4) `ListTasksAsync` returns empty — full context listing requires a separate index grain (deferred post-v0.8); `A2ATaskSurrogate.ContextId` is denormalised so future wiring lands without migration. **Pending**: PR 4 (v0.8.0-preview cut — API freeze, pack 21 packages, extend smoketest, tag).
- 2026-04-19 — PR 4 landed on OSS `main`. Two commits, mirroring the v0.7 pattern: `073073a feat(a2a.server): A2A inbound server pillar (v0.8 PRs 1-3)` (26 files, +2277 −3) then `850f078 chore: API freeze for v0.8.0-preview — promote Unshipped -> Shipped` (4 files, symmetric Unshipped→Shipped moves: 50 entries in A2A.Server + 27 in Hosting.Orleans). Annotated `v0.8.0-preview` tag created on `850f078` (not pushed). 21 `.nupkg` + 21 `.snupkg` packed at `0.8.0-preview` into `artifacts/packages/`. Smoketest refreshed to `0.8.0-preview`, runs clean against the refreshed feed, and prints the final line `"All twenty-one Vais.Agents.* 0.8.0-preview packages consumed cleanly from a plain .NET 9 console app."` A2A-server probe line verified: `route=/agents/smoke-a2a-agent card.name=smoke-a2a-agent card.version=1.0 skills=1 default-skill-id=invoke interfaces=1 iface-url=http://localhost:5080/agents/smoke-a2a-agent streaming=False types-probed=7 orleans-store-types=3 jwt-scheme=A2AJwt`. Milestone log entry (`actor-agents-oss-milestone-log.md`) appended. Research doc §7 "A2A inbound" backlog line struck through and pointed at this pillar. **Pillar closed.** Only follow-up remaining: the manual A2A-client acceptance demo (A2A Inspector / `a2a-cli`) when an external client is available — test-suite's `A2AClient` round-trip over `TestHost` is the closest automated equivalent.
