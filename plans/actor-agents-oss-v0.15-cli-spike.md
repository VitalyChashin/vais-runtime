# v0.15 CLI (`vais` over the HTTP client) — research spike

Scoped research pass before committing to a v0.15 pillar plan. Companion to [`actor-agents-oss-extraction-research.md`](./actor-agents-oss-extraction-research.md) §7 backlog: *"CLI (`vais apply / get / invoke / logs / signal`) over the HTTP client."* First first-party command-line surface for the shipped v0.6 HTTP control plane + v0.12 SSE streaming. Created 2026-04-20.

---

## Why a spike before a pillar

The §7 line is one sentence covering a whole new shipped tool surface — framework choice, verb set, auth model, config file shape, output-format contract, tool distribution mode. Each of these anchors consumer expectations and is expensive to reverse post-freeze:

- **Framework** — the choice locks a dependency into every consumer's `dotnet tool install`. Spectre.Console.Cli and System.CommandLine are close competitors with different rendering + extensibility stories.
- **Verb semantics** — `vais logs` in particular has no obvious mapping to the shipped runtime surface (audit vs. SSE-attach vs. journal-replay). Picking the wrong one creates consumer muscle memory we can't cheaply undo.
- **Auth + config** — `kubectl`-shape contexts vs. flat config; static-token vs. OIDC device-flow-from-day-one. Cross-platform config paths (`~/.vais/` vs. `%APPDATA%/.vais/`) need a decision.
- **Tool distribution** — dotnet tool only, or also self-contained-publish exe? Public-feed vs. local-only?

Spike output: findings doc + 10 locked decisions + proposed PR shape. No public surface change, no package bumps, no tag.

---

## Current state (confirmed before spike)

Verified as of 2026-04-20 (`v0.14.0-preview` on OSS `main`):

- **HTTP client** (`Vais.Agents.Control.Http.Client`): `IAgentControlPlaneClient` with 7 lifecycle verbs — `CreateAsync`, `ListAsync(labelPrefix, limit)`, `QueryAsync(id, version)`, `UpdateAsync`, `CancelAsync`, `EvictAsync`, `InvokeAsync`. Plus v0.11 DIM overloads with `idempotencyKey`. Plus v0.12 streaming: `InvokeStreamAsync` (text-only) + `InvokeStreamEventsAsync` (full `AgentEvent` stream).
- **Manifest loaders** (`Vais.Agents.Control.Manifests.Json` + `.Yaml`): `IAgentManifestLoader` + `AgentManifestValidationException`. `vais apply -f agent.yaml` routes file → loader → `CreateAsync` / `UpdateAsync`.
- **Auth integration**: control plane is behind `AddAgentControlPlaneJwtAuth` (v0.6). CLI carries the bearer token on outbound requests.
- **Streaming event shapes** (`Vais.Agents.Abstractions`): closed `AgentEvent` hierarchy with 10 subtypes (`TurnStarted` / `CompletionDelta` / `ToolCallStarted` / `ToolCallCompleted` / `GuardrailTriggered` / `InterruptRaised` / `HandoffRequested` / `ToolCallReplayed` / `TurnCompleted` / `TurnFailed`).
- **Zero CLI code**: no `Vais.Agents.Cli` package exists. Greenfield.
- **Prior-art precedent**: `kubectl` (Go), `helm` (Go), `dotnet` CLI. All three have kubectl-shape contexts + apply/get/delete verb muscle memory + `-o json|yaml` output format. Following that idiom for a .NET-shop audience.

---

## Five blocking questions

1. **Q1 — Framework.** Three candidates:
   - **(a) `Spectre.Console.Cli` 0.55.0** — mature community library; built-in table / tree / panel / progress rendering via `IAnsiConsole`; command-class pattern with `[Description]` / `[CommandOption]` attrs; Apache 2.0; widely used (`dotnet-outdated`, `dotnet-grpc`, many internal tools). Targets netstandard2.0 / net8.0 / net9.0 / net10.0.
   - **(b) `System.CommandLine`** — Microsoft's official parser, GA in .NET 10. Parser-only; leaves rendering to the consumer. Smaller dep closure but sparser UX story out of the box.
   - **(c) Cocona / CommandLineParser** — smaller user bases; no compelling advantage here.
   - Lean: **(a) Spectre.Console.Cli** — the kubectl-class UX (coloured tables, progress, panels for errors) matters for a CLI that users live in; Spectre gives it for free; the extra dep (~200KB) is fine for a dotnet tool.

2. **Q2 — Verb set + shape.** Seven lifecycle verbs + streaming attach + config subcommand. Full list:
   - `vais apply -f <file>` — create OR update; server decides via id+version match. Mirrors `kubectl apply`.
   - `vais get agents [name] [-o json|yaml|table]` — list (no name) or fetch single.
   - `vais invoke <id> --text "..." [--session <id>] [--stream] [-o text|json] [--version <v>]`.
   - `vais logs <id>` — **semantics locked in Q3.**
   - `vais signal <id> --kind <kind> --payload '{...}'`.
   - `vais delete <id> [--version <v>] [--force]` — maps to `EvictAsync`. Prompts confirm unless `--force`.
   - `vais cancel <id> [--version <v>]` — maps to `CancelAsync`. Not destructive; no prompt.
   - `vais config use-context <name>` / `vais config current-context` / `vais config set-context <name> [options]` / `vais config get-contexts`.
   - `vais init <name> [-o <file>]` — scaffolds a starter YAML manifest (tiny, high-UX-value).
   - `vais version` — prints CLI + shipped-client version.
   - Lean: full verb parity + `init` + `config` + `version`. ~11 subcommands total.

3. **Q3 — What are `logs`?** Three candidates:
   - **(a) Live-run SSE attach** — alias for `vais invoke <id> --stream` attached to a session id. Prints events until `turn.completed` / `turn.failed`. Uses v0.12 streaming; zero runtime-side changes.
   - **(b) Control-plane audit log** — query `LoggerAuditLog`-equivalent entries. Requires a new HTTP surface (`GET /v1/audit`) shipped by the runtime.
   - **(c) Run-history replay from journal** — replay `IAgentJournal` entries for a given `runId`. Requires a new HTTP surface (`GET /v1/agents/{id}/runs/{runId}/journal`) + run-query surface (same blocker as AgentRun CRD).
   - Lean: **(a) live-run SSE attach** — no new runtime surface; reuses v0.12. Semantic: `vais logs <agentId> [--session <id>]` opens an SSE stream and prints events with a configurable filter (`--only delta,tool.completed`). If `--session` is omitted, streams the next run the caller-id invokes. Audit + journal-replay are v0.16+ alongside the deferred run-registry endpoint.

4. **Q4 — Config + auth.**
   - **Config file**: `~/.vais/config.yaml` (Unix), `%USERPROFILE%\.vais\config.yaml` (Windows). `kubectl`-shape — top-level `{clusters, users, contexts, current-context}` so users switch envs with `vais config use-context`. Single file, single well-known path; env-var override via `VAIS_CONFIG`.
   - **Auth schemes for MVP**: (a) static bearer token on `users.<name>.token`; (b) env-var `VAIS_TOKEN` override. Precedence: `--token` flag > `VAIS_TOKEN` env > active context user's token > unauthenticated.
   - **OIDC device-flow** + **K8s SA projected-token** + **exec plugin** (kubectl-style) all deferred to v0.15.1+.
   - Lean: static token + env override + kubectl-shape contexts. First-time setup documented as `vais config set-context default --server http://localhost:5080 --token <jwt>` + `vais config use-context default`.

5. **Q5 — Package shape + testing.**
   - One new NuGet package `Vais.Agents.Cli`. `<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>`. Install: `dotnet tool install -g Vais.Agents.Cli --version 0.15.0-preview`.
   - Dependencies: `Vais.Agents.Control.Http.Client` + `Vais.Agents.Control.Manifests.Json` + `Vais.Agents.Control.Manifests.Yaml` + `Spectre.Console.Cli 0.55.0` + `Microsoft.Extensions.Http` (already transitively pulled). ~2MB packed.
   - Public types: minimal — just the `Program` entry point + a single `AddVaisCli` extension for testability. Commands live internal.
   - Test project `Vais.Agents.Cli.Tests` — Spectre provides `CommandAppTester` for invoking commands with canned args + capturing console output. Mock `IAgentControlPlaneClient` for verb dispatch coverage.
   - No integration-test project. The Http.Client is already covered; the CLI layer is wiring.
   - Package count: **24 → 25**.
   - Lean: single library package as a dotnet tool + unit tests with Spectre's test harness.

---

## Tasks (research + archetype exercises)

- [x] **Q1 — Framework audit.** Spectre.Console.Cli 0.55.0 verified on NuGet (targets netstandard2.0 / net8.0 / net9.0 / net10.0). Prototyped `vais version` in both Spectre and System.CommandLine — LoC comparable (15 vs. 18). Spectre wins on rendering (coloured tables, panels, progress) for the ~200KB cost. Cloud-CLI ecosystem leans toward Spectre.
- [x] **Q2 — Verb table.** Full 14-subcommand table landed in findings doc §Q2 — maps each verb to exactly one `IAgentControlPlaneClient` call or local config mutation. Zero gaps in the HTTP client; no runtime-side changes needed.
- [x] **Q3 — `vais logs` proof.** SSE-attach sketch in findings doc §Q3. Uses `InvokeStreamEventsAsync`; Ctrl-C → `CancellationTokenSource` → `OperationCanceledException` → exit code 130. Event-kind filter + colour rendering per event subtype. `--since <ts>` is client-side filter only (v0.12 SSE has no replay).
- [x] **Q4 — Config file shape + OS paths.** Schema locked as `{apiVersion: "vais.io/v1", kind: "Config", currentContext, clusters[], users[], contexts[]}`. `Environment.SpecialFolder.UserProfile` resolves cleanly across Linux / macOS / Windows. `VAIS_CONFIG` env var overrides.
- [x] **Q5 — Packaging audit.** `<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>` confirmed as the dotnet-tool pattern. Spectre's `CommandAppTester` surface checked — supports canned-args invocation + output capture + registrar injection for mocking `IAgentControlPlaneClient`. No integration-tests project needed.
- [x] **Findings doc.** [`actor-agents-oss-v0.15-cli-findings.md`](./actor-agents-oss-v0.15-cli-findings.md) — Q1–Q5 synthesis + 10 locked decisions + proposed 4-PR pillar shape + ~2-2.5-day effort estimate.

---

## Exit criteria

- [x] All five questions answered with evidence (not opinion) — Q1 from framework NuGet audit + prototype LoC comparison; Q2 from full verb table mapped to shipped client API (zero gaps); Q3 from SSE-attach sketch + POSIX exit-code verification; Q4 from config schema draft + cross-platform `SpecialFolder.UserProfile` audit; Q5 from `<PackAsTool>` pattern + `CommandAppTester` API check.
- [x] Recommendation lands: **ready to write v0.15 pillar plan.** 10 decisions locked in findings doc.

No public surface change. No package bumps. No tag.

---

## Progress log

- 2026-04-20 — spike plan created after design conversation. Five blocking questions scoped (framework, verb set, `logs` semantic, config/auth, package shape). Lean positions recorded: Spectre.Console.Cli 0.55.0; full 11-subcommand set (apply/get/invoke/logs/signal/delete/cancel/config{4}/init/version); `logs` = live-run SSE attach via v0.12 streaming; kubectl-shape contexts config at `~/.vais/config.yaml` with static-token + env-override MVP auth; dotnet tool distribution (`vais` command), package count 24 → 25. Findings doc pending.
- 2026-04-20 — Spike complete. All five leans held up. Q1: Spectre.Console.Cli 0.55.0 on net9.0; prototype `vais version` shows LoC parity with System.CommandLine but Spectre's rendering story wins for a cloud-CLI UX. Q2: full 14-subcommand verb table maps cleanly to `IAgentControlPlaneClient` — zero gaps; no new runtime endpoints. Q3: `logs` = live-run SSE attach via v0.12; Ctrl-C → `CancellationTokenSource` → exit 130; `--since` client-side filter only. Q4: `~/.vais/config.yaml` with kubectl-shape `{clusters, users, contexts, currentContext}` + `VAIS_CONFIG` override + precedence chain `--token` > `VAIS_TOKEN` > context user > unauthenticated. Q5: dotnet tool pattern (`<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>`) verified; Spectre `CommandAppTester` surface sufficient for unit tests; no integration-tests project needed. Findings doc landed with 10 locked decisions + proposed 4-PR pillar shape (PR 1 skeleton + config subcommand + version; PR 2 apply/get/delete/cancel/init + auth plumbing; PR 3 invoke/logs/signal + streaming; PR 4 v0.15.0-preview cut). Effort estimate: ~2-2.5 days. One new library package + test project. **Ready to write v0.15 pillar plan.**
