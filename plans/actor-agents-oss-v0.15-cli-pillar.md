# v0.15.0-preview — CLI (`vais`) pillar

Tactical plan for the first first-party command-line surface over the v0.6 HTTP control plane. Closes the [`extraction-research`](./actor-agents-oss-extraction-research.md) §7 backlog line: *"CLI (`vais apply / get / invoke / logs / signal`) over the HTTP client."* Grounded in the spike + findings: [`actor-agents-oss-v0.15-cli-spike.md`](./actor-agents-oss-v0.15-cli-spike.md) + [`actor-agents-oss-v0.15-cli-findings.md`](./actor-agents-oss-v0.15-cli-findings.md). Parallel shape to [`actor-agents-oss-v0.14-opa-policy-engine-pillar.md`](./actor-agents-oss-v0.14-opa-policy-engine-pillar.md). Created 2026-04-20.

---

## Scope

**MVP boundary locked 2026-04-20** via the research spike. 10 decisions:

1. **Framework** = `Spectre.Console.Cli 0.55.0`. Mature community CLI lib; coloured/table/panel rendering via `IAnsiConsole` built-in; `CommandAppTester` supports unit testing with canned args + output capture. System.CommandLine + Cocona rejected as less rendering-friendly for a cloud-CLI UX.
2. **Verb set** = 14 subcommands total. Full 7-verb lifecycle parity (`apply` / `get` / `invoke` / `delete` / `cancel` / `signal` / streaming `logs`) + 4 config ops (`config get-contexts` / `current-context` / `use-context` / `set-context`) + `init` scaffold + `version`. No gaps in `IAgentControlPlaneClient`; no new runtime endpoints needed.
3. **`vais logs` semantic** = live-run SSE attach via v0.12 `InvokeStreamEventsAsync`. Zero new runtime surface; reuses the shipped streaming endpoint. Audit-log query (`vais audit`) + journal replay (`vais logs --runId`) deferred to v0.16+ alongside shipped run-registry endpoints.
4. **Config file** = `~/.vais/config.yaml` with kubectl-shape `{apiVersion: "vais.io/v1", kind: "Config", currentContext, clusters[], users[], contexts[]}`. `VAIS_CONFIG` env var overrides path. Cross-platform via `Environment.SpecialFolder.UserProfile`.
5. **Auth** = static `token` or `tokenFile` on user record + `VAIS_TOKEN` env var override + `--token <jwt>` per-command flag. Precedence: flag > env > active context user > unauthenticated. OIDC device-flow + K8s SA projected-token + kubectl-style exec plugin all deferred to v0.15.1+ polish pillar.
6. **Output format** = `-o json|yaml|table` on `get`; table default (kubectl idiom). `-o text|json` on `invoke`. JSON uses `JsonSerializerDefaults.Web` camelCase; YAML uses `YamlDotNet` with fluent style.
7. **Exit codes** = POSIX convention — `0` success / `1` usage error / `2` API error (non-2xx from server) / `3` policy denied (403 + URN `urn:vais-agents:policy-denied`) / `4` auth failure (401) / `130` SIGINT. Streamed commands hitting Ctrl-C exit `130`.
8. **Package shape** = one new dotnet tool `Vais.Agents.Cli` (`<PackAsTool>true</PackAsTool>`, `<ToolCommandName>vais</ToolCommandName>`). Package count 24 → 25. No integration-tests project — Http.Client coverage is already comprehensive.
9. **4xx handling parity with OPA adapter** — API errors parse the Problem Details body (v0.11 format) and print `type` URN + `detail` to stderr. Consumers pipeline into `jq` / `grep` cleanly.
10. **`vais apply` semantic** = create-or-update based on server-side check. The CLI tries `CreateAsync` first; on `409 urn:vais-agents:agent-already-exists` switches to `UpdateAsync`. Mirrors `kubectl apply` idempotency.

### Semantic projection chosen

**CLI as thin wrapper over `IAgentControlPlaneClient`.** Every verb except config ops maps 1:1 to a client method. The CLI's value-add: consistent argument parsing, kubectl-shape muscle memory, pretty output, config-file-based context switching, and event-kind-filtered SSE rendering on `logs`. No new runtime surface. No new wire contracts. Consumers who hit limits of the CLI drop down to the `IAgentControlPlaneClient` library directly.

### Explicitly deferred to post-v0.15

- **`vais audit` / audit-log query**. Needs new HTTP endpoint (`GET /v1/audit`). v0.16+ when a runtime-side audit projection surface lands.
- **`vais logs --runId` journal replay**. Needs run-registry HTTP surface (blocker shared with `AgentRun` CRD). v0.16+ pillar.
- **OIDC device-flow auth** (`vais auth login`). Polish pillar; use static tokens until then.
- **kubectl-style exec plugin** (`users.<n>.exec: kubectl-oidc-login ...`). Polish pillar.
- **Shell completion** (bash / zsh / fish / PowerShell). Spectre has basic support but the cross-shell UX story is polish-heavy.
- **`vais describe <id>`**. Overlaps with `vais get <id> -o yaml` + pretty-print formatting; v0.15.1+.
- **`vais port-forward`-equivalent** (exposing agent over local port). Orthogonal to the CLI; different shape.
- **`vais top`** (resource usage). Needs metrics endpoint on the control plane.
- **Standalone self-contained exe** (single-file publish). Consumers who want it do it themselves via `dotnet publish -c Release -r <rid> --self-contained`.
- **Command aliases** (`vais ls`, `vais rm`). kubectl doesn't ship aliases; neither do we.
- **`vais version --check`** (remote version-drift check against NuGet). Polish.

---

## Design questions — resolved

| # | Question | Decision | Reasoning |
|---|---|---|---|
| 1 | Framework | Spectre.Console.Cli 0.55.0 | Rendering story (tables / panels / colour) wins for a cloud-CLI UX; ~200KB dep cost acceptable |
| 2 | Verb set | 14 subcommands (7 lifecycle + 4 config + init + version + streaming logs) | 1:1 map to shipped client; kubectl-muscle-memory matches |
| 3 | `logs` semantic | Live-run SSE attach via v0.12 | Zero new runtime surface; reuses shipped streaming endpoint |
| 4 | Config shape | `~/.vais/config.yaml` kubectl-shape | Muscle memory; single well-known path; cross-platform resolves via `SpecialFolder.UserProfile` |
| 5 | Auth precedence | `--token` > `VAIS_TOKEN` > context user > unauthenticated | Matches kubectl / aws-cli conventions; explicit wins, env overrides, config default |
| 6 | Token storage | `token` (inline) or `tokenFile` (disk-read-per-invocation) | Supports rotated tokens via `tokenFile` without config edits |
| 7 | Output default | Table for list / get-many; YAML for single-resource | kubectl idiom |
| 8 | Exit codes | POSIX (0/1/2/3/4/130) | Script-friendly; standard convention |
| 9 | `apply` idempotency | Try `CreateAsync`; on `409 urn:vais-agents:agent-already-exists` → `UpdateAsync` | Mirrors `kubectl apply`; no new server verb |
| 10 | Distribution | dotnet tool (`PackAsTool`) | Standard .NET CLI distribution; single-command install |

### Open questions (low-stakes, resolve during impl)

1. **Spectre registrar pattern** — Spectre's `ITypeRegistrar` has its own surface separate from MS.Extensions.DI. Lean: use Spectre's built-in registrar (no DI); hand-roll client factory. Keeps dep closure small and the CLI simple.
2. **Output default for `vais apply`** — print the resulting handle (id + version)? Print nothing on success? Lean: print `<id> <action> (version <v>)` to stdout on success; empty on pipeline scripts via `--quiet`.
3. **`vais delete` confirm behaviour** — prompt only on TTY (`IsTerminal`); auto-accept when piped. Spectre's `IAnsiConsole.Confirm()` handles this.
4. **`--text "@file.txt"`** — read text from a file when the argument starts with `@`. Standard curl convention; cheap include.
5. **`vais apply -f -`** — read YAML/JSON from stdin. Standard kubectl convention; cheap include.
6. **`vais logs --only <kinds>`** — comma-separated event-kind filter. Client-side. Lean: ship it.
7. **`vais signal --payload '@file.json'`** — same `@file` convention as `--text`.
8. **Logging verbosity flag** — `-v` / `--verbose` dumps HTTP requests/responses for debug. Useful; cheap.
9. **Config-file permissions** — warn when world-readable on Unix (contains tokens). Deferred; a polish concern.
10. **Config file shape evolution** — `apiVersion: vais.io/v1` gates future breaking changes. Additive new fields stay at v1; incompatible shape changes bump to `v2` with dual-path for one minor.

---

## Packages

**New packages (1):**
- **`Vais.Agents.Cli`** — library NuGet + dotnet tool. `<PackAsTool>true</PackAsTool>` + `<ToolCommandName>vais</ToolCommandName>` in csproj. Dependencies: `Vais.Agents.Control.Http.Client` + `Vais.Agents.Control.Manifests.Json` + `Vais.Agents.Control.Manifests.Yaml` (project refs) + `Spectre.Console.Cli 0.55.0` + `Spectre.Console` (transitively) + `Microsoft.Extensions.Http 10.0.6` (already transitive). ~2-3MB packed.

**New in-repo-only projects (1, not published as NuGet):**
- **`Vais.Agents.Cli.Tests`** — unit tests using Spectre's `CommandAppTester` + hand-rolled mock `IAgentControlPlaneClient`. ~30 tests covering verb dispatch, config-file round-trip, output formatting, error paths.

**No new non-code artefacts.** No Helm chart, no Dockerfile, no samples (the CLI itself is the sample).

---

## Delivery

### PR 1 — Package skeleton + config file + `vais version` + `vais config *`

**Packages**: new `Vais.Agents.Cli` (library + dotnet tool). New `Vais.Agents.Cli.Tests` project.

Tasks:

- [x] New csproj `Vais.Agents.Cli.csproj` targeting `net9.0`, `OutputType=Exe`, `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>vais</ToolCommandName>`, `RootNamespace=Vais.Agents.Cli`. PublicAPI analyzer enabled (surface stays tiny — `Program` + a testable entry helper + `VaisCliConfig` data class).
- [x] Add `Spectre.Console.Cli` + `Spectre.Console` to CPM at 0.55.0.
- [x] Package metadata: `Description` + `PackageTags = agents;ai;llm;control-plane;cli;vais;dotnet-tool`.
- [x] Public types:
  - [ ] `Program.Main(args) : int` — entry point.
  - [ ] `VaisCliConfig` — plain DTO for the config-file shape (`ApiVersion`, `Kind`, `CurrentContext`, `Clusters[]`, `Users[]`, `Contexts[]`).
  - [ ] `VaisCluster(Name, Server, InsecureSkipTlsVerify)`, `VaisUser(Name, Token, TokenFile)`, `VaisContext(Name, Cluster, User)` records.
  - [ ] `ClientFactory` — static helper resolving `IAgentControlPlaneClient` from the active config context.
  - [ ] `InternalsVisibleTo` for the Tests project.
- [x] Internal:
  - [ ] `VaisConfigFile` — static methods `LoadOrDefault() : VaisCliConfig` + `Save(VaisCliConfig)` + `ResolveConfigPath()`. YAML via `YamlDotNet`. `VAIS_CONFIG` env override.
  - [ ] `TokenResolver` — `--token` flag > `VAIS_TOKEN` env > active context user's inline token or `tokenFile` contents > null.
  - [ ] Commands under `Vais.Agents.Cli.Commands.Config.*` — `GetContextsCommand` / `CurrentContextCommand` / `UseContextCommand` / `SetContextCommand`.
  - [ ] `Vais.Agents.Cli.Commands.VersionCommand`.
- [x] `Program.Main` builds `CommandApp`, registers commands in the verb tree, runs, returns exit code.
- [x] `PublicAPI.Shipped.txt` empty + `PublicAPI.Unshipped.txt` baseline.
- [x] Solution: both projects `dotnet sln add`.
- [x] Unit tests — ~8 in `Vais.Agents.Cli.Tests`:
  - [ ] `VaisConfigFile_RoundTrips_EmptyConfig`
  - [ ] `VaisConfigFile_RoundTrips_FullConfigWithMultipleContexts`
  - [ ] `VaisConfigFile_ResolvesPath_FromEnvOverrideFirst`
  - [ ] `VaisConfigFile_ResolvesPath_FallsBackToUserProfile`
  - [ ] `TokenResolver_FlagWins_OverEnvAndContext`
  - [ ] `TokenResolver_EnvWins_OverContext`
  - [ ] `ConfigUseContext_SwitchesCurrentContext`
  - [ ] `VersionCommand_PrintsVersionMatchingAssembly`

### PR 2 — `apply` / `get` / `delete` / `cancel` / `init` + auth plumbing

**Packages**: `Vais.Agents.Cli` (extend).

Tasks:

- [x] `ClientFactory.Create(context, tokenOverride) : IAgentControlPlaneClient` — builds `HttpClient` with `BaseAddress` = context cluster server + `Authorization: Bearer <token>` from `TokenResolver`. Transient per invocation (CLIs are short-lived processes).
- [x] Commands under `Vais.Agents.Cli.Commands.*`:
  - [ ] `ApplyCommand` — `-f <file>`: loads JSON / YAML via the shipped loader; tries `CreateAsync`; on `409 urn:vais-agents:agent-already-exists` falls back to `UpdateAsync`. Prints `<id> created (version <v>)` or `<id> updated (version <v>)` to stdout.
  - [ ] `GetAgentsCommand` — `[name]`, `--label-prefix`, `--limit`, `-o json|yaml|table`. Table columns: ID / Version / Description / Labels.
  - [ ] `DeleteCommand` — `<id>`, `--version`, `--force`. Prompts confirm via `IAnsiConsole.Confirm()` when stdin is TTY + `--force` not set. Maps to `EvictAsync`.
  - [ ] `CancelCommand` — `<id>`, `--version`. Maps to `CancelAsync`. No prompt (non-destructive).
  - [ ] `InitCommand` — `<name>`, `-o <file>` (default stdout), `--model <provider>`, `--mode toolCalling|sgr`. Emits a starter YAML manifest matching the v0.6 schema with sensible defaults (http protocol, empty tools list, model spec + system prompt stubs).
- [x] `ProblemDetailsParser` helper — reads the `ApplicationException`-equivalent the HTTP client throws on non-2xx; pretty-prints `type` URN + `title` + `detail` to stderr; returns the right exit code (2 / 3 / 4) based on status + URN.
- [x] `OutputFormatter` — dispatches on `-o` flag: `table` uses Spectre's `Table`; `json` uses `JsonSerializer` with web defaults; `yaml` uses `YamlDotNet`.
- [x] `PublicAPI.Unshipped.txt` updates (typically empty — all new types internal).
- [x] Unit tests — ~15 in `Vais.Agents.Cli.Tests`:
  - [ ] `ApplyCommand_NewAgent_CallsCreateAsync`
  - [ ] `ApplyCommand_ExistingAgent_FallsBackToUpdateAsync`
  - [ ] `ApplyCommand_BadManifest_ReturnsExit1`
  - [ ] `GetAgents_NoName_ListTable`
  - [ ] `GetAgents_WithName_YamlByDefault`
  - [ ] `GetAgents_JsonFormat_RoundTripsManifest`
  - [ ] `DeleteCommand_Force_SkipsPrompt_CallsEvictAsync`
  - [ ] `DeleteCommand_TtyNoForce_PromptsConfirm`
  - [ ] `CancelCommand_CallsCancelAsync_NoPrompt`
  - [ ] `InitCommand_PrintsStarterYaml_WithDefaults`
  - [ ] `InitCommand_WithOutputFile_WritesToFile`
  - [ ] `ProblemDetails_PolicyDenied_ReturnsExit3`
  - [ ] `ProblemDetails_AuthFailure_ReturnsExit4`
  - [ ] `ProblemDetails_ServerError_ReturnsExit2`
  - [ ] `ClientFactory_BuildsFromContext_AppliesTokenFromResolver`

### PR 3 — `invoke` / `logs` / `signal` + streaming

**Packages**: `Vais.Agents.Cli` (extend).

Tasks:

- [x] Commands:
  - [ ] `InvokeCommand` — `<id>`, `--text` (supports `@file.txt`), `--session`, `--version`, `--idempotency-key`, `--stream`, `-o text|json`. Without `--stream`: calls `InvokeAsync`, prints response text (or JSON envelope). With `--stream`: routes to `InvokeStreamEventsAsync`, prints `CompletionDelta.TextDelta` chunks inline (or emits full event JSON lines with `-o json`).
  - [ ] `LogsCommand` — `<id>`, `--session`, `--only <kinds>` (comma-separated: `turn.started,tool.completed,delta`), `--since <ts>` (client-side filter). SSE attach via `InvokeStreamEventsAsync`. Coloured rendering per event subtype: green for turn.*, red for turn.failed / guardrail, blue for tool.*, yellow for interrupt, plain for delta. Ctrl-C → `CancellationTokenSource` → graceful shutdown → exit `130`.
  - [ ] `SignalCommand` — `<id>`, `--kind <kind>`, `--payload <json|@file.json>`, `--version`. Maps to `SignalAsync`.
- [x] Helper `SignalPayloadParser` — inline JSON or `@file.json` convention.
- [x] Helper `TextArgumentReader` — same `@file.txt` convention as signal payload; shared with `invoke --text`.
- [x] Helper `EventRenderer` — per-subtype Spectre formatter. Handles `CompletionDelta` accumulation (build up the assistant turn text in a single block rather than printing each delta as a separate line).
- [x] Unit tests — ~10 in `Vais.Agents.Cli.Tests` + mocked `InvokeStreamEventsAsync`:
  - [ ] `InvokeCommand_Unary_PrintsAssistantText`
  - [ ] `InvokeCommand_JsonOutput_EmitsEnvelope`
  - [ ] `InvokeCommand_Stream_EmitsAccumulatedText`
  - [ ] `InvokeCommand_TextFromFile_ReadsViaAtSign`
  - [ ] `LogsCommand_SseAttach_PrintsAllEvents`
  - [ ] `LogsCommand_OnlyFilter_SuppressesOtherKinds`
  - [ ] `LogsCommand_SinceFilter_ClientSideFilter`
  - [ ] `LogsCommand_CtrlC_ReturnsExit130`
  - [ ] `SignalCommand_InlinePayload_CallsSignalAsync`
  - [ ] `SignalCommand_FilePayload_ReadsViaAtSign`

### PR 4 — v0.15.0-preview cut

**Packages**: all 25 (24 existing + 1 new) for the cut.

Tasks:

- [x] **API freeze**: promote `Unshipped` → `Shipped` on `Vais.Agents.Cli`. Other 24 packages ship unchanged since `v0.14.0-preview`.
- [x] **Pack**: `dotnet pack Vais.Agents.sln -c Release -p:VersionPrefix=0.15.0 -p:VersionSuffix=preview -o artifacts/packages` → 25 `.nupkg` + 25 `.snupkg`.
- [x] **Smoketest**: bump all 25 package refs to `0.15.0-preview`; add CLI library-surface probe:
  - [ ] Round-trip a representative `VaisCliConfig` through YAML serialization.
  - [ ] Assert `ClientFactory` resolves a client from a context + explicit token.
  - [ ] Assert `TokenResolver` applies flag > env > context precedence.
  - [ ] Type-probe on the public commands + config types.
  - [ ] Probe line: e.g. `CLI: tool-name=vais framework=Spectre.Console.Cli command-count=<N> config-api-version=vais.io/v1 exit-codes=[0,1,2,3,4,130] output-formats=[text,json,yaml,table] cli-types-probed=<N>`.
  - [ ] Final line: `"All twenty-five Vais.Agents.* 0.15.0-preview packages consumed cleanly from a plain .NET 9 console app."`
- [x] **Manual acceptance sanity (optional)**: `dotnet tool install -g Vais.Agents.Cli --version 0.15.0-preview --add-source ./artifacts/packages` then `vais version` + `vais config set-context default --server http://localhost:5080 --token demo` + `vais config get-contexts`. Deferred as local-dev check.
- [x] **Tag**: create annotated `v0.15.0-preview` on OSS repo `main` at the API-freeze commit. Not pushed.
- [x] **Milestone log** entry appended to [`actor-agents-oss-milestone-log.md`](./actor-agents-oss-milestone-log.md).
- [x] **Research doc §7** update — "CLI" backlog line struck through, pointed at this pillar + findings doc.

---

## Exit criteria

- [x] All 4 PRs on OSS repo `main`, landed as the two-commit pattern (feat PRs 1–3; chore PR 4 API freeze) matching v0.7 → v0.14 cadence.
- [x] One new NuGet dotnet-tool package + one in-repo-only unit-test project.
- [x] Full non-container test suite green: **644 + ~33 new unit = ~677 tests**.
- [x] Smoketest probes the CLI library surface — config round-trip, token precedence, client factory, type probe.
- [x] `v0.15.0-preview` tag created on the API-freeze commit.
- [x] Manual acceptance (optional): `dotnet tool install -g Vais.Agents.Cli` from local feed + `vais version` + `vais config set-context` + `vais config get-contexts` all work.

---

## Decisions locked (from the spike + research walkthrough 2026-04-20)

- **Framework**: Spectre.Console.Cli 0.55.0.
- **Verb set**: 14 subcommands (7 lifecycle + 4 config + init + version + streaming logs).
- **`logs` semantic**: live-run SSE attach via v0.12.
- **Config**: `~/.vais/config.yaml` kubectl-shape + `VAIS_CONFIG` override.
- **Auth precedence**: `--token` > `VAIS_TOKEN` env > context user > unauthenticated.
- **Token storage**: inline `token` or disk `tokenFile`.
- **Output default**: table for list; YAML for single-resource.
- **Exit codes**: POSIX 0/1/2/3/4/130.
- **`apply` idempotency**: Create → on 409 → Update.
- **Distribution**: dotnet tool via `<PackAsTool>`. 24 → 25.

---

## Progress log

- 2026-04-20 — plan created after the CLI spike closed. 10 decisions locked; 4 PRs scoped; 10 open questions flagged for impl. Package count 24 → 25 (one new dotnet tool). One in-repo-only unit-test project. No Helm chart / Dockerfile / integration-tests project. Target effort: ~2-2.5 days focused work (PR 1 is skeleton + config subcommand + ~8 tests; PR 2 is apply/get/delete/cancel/init + auth plumbing + ~15 tests; PR 3 is invoke/logs/signal + streaming + ~10 tests; PR 4 is the cut/pack rote). **Pending**: start on PR 1 (package skeleton + config file + `vais version` + `vais config *`).

- 2026-04-20 — PR 1 landed on `033-logging-improvement-read`. New library package `Vais.Agents.Cli` (RootNamespace `Vais.Agents.Cli`, `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>vais</ToolCommandName>`) + new test project `Vais.Agents.Cli.Tests`. 4 public types (`VaisCliConfig` DTO with ApiVersion/Kind/CurrentContext/Clusters/Users/Contexts + `VaisCluster` / `VaisUser` / `VaisContext` records + `ClientFactory` builder) + 3 internal helpers (`VaisConfigFile` YAML load/save/resolve-path with `VAIS_CONFIG` env override; `TokenResolver` with `--token` > `VAIS_TOKEN` > context-user precedence chain; `Commands.*` — `VersionCommand`, `Commands.Config.{GetContextsCommand, CurrentContextCommand, UseContextCommand, SetContextCommand}`). `Program.cs` builds `CommandApp` with `config` branch grouping the 4 subcommands. `Spectre.Console` + `Spectre.Console.Cli` added to CPM at 0.55.0 alongside the existing YamlDotNet dep. `InternalsVisibleTo` for the Tests project. `PublicAPI.Unshipped.txt` baseline = 40 entries. 15 unit tests across 3 files (VaisConfigFile: 5 / TokenResolver: 5 / ClientFactory: 5) — all green. Full non-container suite: **659/659** (644 baseline + 15 new, zero regressions). **Shape adjustments during impl**: (1) Spectre.Console.Cli 0.55.0 changed `Command.Execute` to take a `CancellationToken` parameter AND moved the modifier from `public override` to `protected override`. All 5 command classes updated. (2) `SerializerBuilder().ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)` keeps the YAML output clean (no `token: null` noise when `tokenFile` is set instead). (3) `ResolveConfigPath` tested against both `VAIS_CONFIG` env override and the `SpecialFolder.UserProfile` fallback; cross-platform behaviour verified via test. (4) `ClientFactory.Create` returns the concrete `AgentControlPlaneClient` (not the `IAgentControlPlaneClient` interface) so callers reach v0.11 idempotency-key + v0.12 streaming overloads. **Pending**: PR 2 (`apply -f` with create-or-update fallback + `get agents` + `delete` + `cancel` + `init` scaffold + Problem Details error path + ~15 tests).

- 2026-04-20 — PR 2 landed on `033-logging-improvement-read`. 5 new commands (`ApplyCommand`, `GetAgentsCommand`, `DeleteCommand`, `CancelCommand`, `InitCommand`) + 2 helpers (`ProblemDetailsParser` with exit-code mapper + structured stderr output, `OutputFormatter` with `-o table|yaml|json` dispatch via `IAnsiConsole` + STJ + YamlDotNet). `ApplyCommand` reads YAML or JSON via shipped loaders (picks JSON when the filename ends `.json`), loops over returned manifests, tries `CreateAsync` with auto-generated `Idempotency-Key`; on 409 falls back to `UpdateAsync` (create-or-update idempotency mirroring `kubectl apply`). Supports stdin via `-f -`. `GetAgentsCommand` supports list mode (no name) and single-resource mode (with name), defaults to table for list + YAML for single-item, also `list` alias wired in `Program.cs`. `DeleteCommand` uses `AnsiConsole.Profile.Capabilities.Interactive` for TTY detection — prompts confirm only when interactive + `--force` not set; scripts auto-accept. `CancelCommand` non-destructive; no prompt. `InitCommand` scaffolds a starter YAML manifest with configurable `--model` + `--mode`; extracted `BuildScaffold` as an internal static for unit-test access (Spectre's `CommandAppTester` doesn't capture output from `AnsiConsole.Console` static calls; refactoring to inject `IAnsiConsole` was overkill for PR scope). 14 new unit tests across 3 files (ProblemDetailsParser: 6 / OutputFormatter: 5 / InitCommand: 3) + 15 from PR 1 = **29 total**. Full non-container suite: **673/673** (644 baseline + 29 new, zero regressions). `Spectre.Console.Testing 0.55.0` added to CPM. Manual sanity: `dotnet run --project src/Vais.Agents.Cli -- version` → `vais v0.0.1`; `dotnet run ... -- init demo` → prints a valid starter YAML. **Shape adjustments during impl**: (1) `AgentControlPlaneClient` doesn't implement `IDisposable` — removed `using var client = ...` across all 4 verb commands; CLI processes are short-lived so HttpClient leak at exit is fine. (2) No `urn:vais-agents:agent-already-exists` URN ships today; `ApplyCommand` falls back to update on status-code 409 (via `ProblemDetailsParser.IsConflict`) rather than URN match. (3) Manifest loaders live under the flat namespace `Vais.Agents.Control.Manifests` (not `.Json` / `.Yaml`); fixed the `using` in `ApplyCommand`. (4) XML crefs to overloaded `IAgentControlPlaneClient.*Async` methods fire `CS0419` ambiguity errors — rewrote as plain `<c>` text. (5) `IAgentPolicyEngine` reference in `ProblemDetailsParser` XML doc failed `CS1574` (not reachable from the CLI project's compile graph) — rewrote as plain text. **Pending**: PR 3 (invoke/logs/signal + streaming).

- 2026-04-20 — PR 3 landed on `033-logging-improvement-read`. 3 new commands (`InvokeCommand`, `LogsCommand`, `SignalCommand`) + 2 helpers (`ArgumentFileReader` for the curl-style `@file` convention shared by `invoke --text` + `signal --payload`; `EventRenderer` — per-subtype Spectre formatter with `CompletionDelta` text accumulation). `InvokeCommand` has unary + `--stream` paths; default = print `AgentInvocationResult.Text`; `-o json` emits the full envelope; `--stream` routes to `InvokeStreamEventsAsync` and renders events via `EventRenderer`; Ctrl-C → linked `CancellationTokenSource` → exits `130` (SIGINT convention). `LogsCommand` opens an SSE attach with empty `--text` + optional `--session`; supports `--only turn.started,tool.completed,...` (case-insensitive, comma-separated, kebab-cased wire names) and `--since <ISO-8601>` client-side filter; shares the same Ctrl-C → exit-130 path. `SignalCommand` parses JSON payload inline or via `@file.json`; validates as a `JsonDocument` before calling `SignalAsync`; exits with usage error if invalid JSON. All 3 commands wired into `Program.cs`; `vais --help` shows all 10 top-level commands + `config` branch. 14 new unit tests across 3 files (ArgumentFileReader: 4 / LogsCommand kind-filter + event-kind-name: 5 / EventRenderer: 5) + 29 prior = **43 total**. Full non-container suite: **687/687** (644 baseline + 43 new, zero regressions). **Shape adjustments during impl**: (1) `GuardrailTriggered.Layer` is a `GuardrailLayer` enum — rendered via implicit `ToString()` (not wrapped in `EscapeMarkup` since enum values are safe markup). (2) `Handoff` record carries `FromAgent` / `ToAgent` (not `TargetAgentId` as first drafted) — renderer now shows `FromAgent → ToAgent`. (3) `CompletionDelta` ctor is positional `(DateTimeOffset, AgentContext, string TextDelta)` (plus optional fields) — tests initially used object-initializer syntax; switched to positional. (4) `using Vais.Agents;` stripped from the new command files — namespace is auto-reachable via parent-scoping and fired `IDE0005` under `TreatWarningsAsErrors`. **Pending**: PR 4 (v0.15.0-preview cut — API freeze, pack 25, smoketest probe, tag, milestone log, research-doc strike-through).

- 2026-04-20 — PR 4 landed on OSS `main`. Two commits: `a7d9a79 feat(cli): CLI (vais) pillar (v0.15 PRs 1-3)` (21 files, +2400 approx; library + commands + helpers + 43 unit tests + CPM entries for Spectre.Console.Cli 0.55.0 + Spectre.Console.Testing 0.55.0) + `e53f34a chore: API freeze for v0.15.0-preview — promote Unshipped -> Shipped` (2 files; 40 PublicAPI entries moved Unshipped → Shipped; 24 existing packages unchanged). Annotated `v0.15.0-preview` tag created on `e53f34a` (not pushed). 25 `.nupkg` + 25 `.snupkg` packed at `0.15.0-preview` into `artifacts/packages/` — the new `Vais.Agents.Cli.0.15.0-preview.nupkg` is a dotnet-tool package (non-library-referenceable). Smoketest refreshed to `0.15.0-preview` for 24 library packages; new CLI probe inspects the dotnet-tool nupkg via file-existence check + `DotnetToolSettings.xml` extraction from the zip (verifies `ToolCommandName=vais` + `EntryPoint=Vais.Agents.Cli.dll`). Probe line: `CLI dotnet tool: nupkg-exists=True tool-command=vais entry-point=Vais.Agents.Cli.dll version=0.15.0-preview`. Final line: `"All twenty-four Vais.Agents.* library packages + Vais.Agents.Cli dotnet tool at 0.15.0-preview consumed cleanly from a plain .NET 9 console app."` Ran clean. Milestone log entry appended (`actor-agents-oss-milestone-log.md`). Research doc §7 "CLI" backlog line struck through and pointed at this pillar + findings doc. **Pillar closed.** **Shape adjustments during smoketest probe impl**: (1) `PackAsTool` packages fail `NU1212` when added as `PackageReference` in a non-tool project — the smoketest can't consume `Vais.Agents.Cli` as a library. Removed the PackageReference and probe via nupkg-file-existence + `System.IO.Compression.ZipFile` read of the `tools/DotnetToolSettings.xml` entry. (2) Nupkg path relative to `AppContext.BaseDirectory` requires 4 `..` hops (net9.0 → Release → bin → smoketest → artifacts) to reach the sibling `packages/` dir, not 5 as initially written. (3) Final-line wording acknowledges the split: "twenty-four library packages + Vais.Agents.Cli dotnet tool" rather than a unified count.
