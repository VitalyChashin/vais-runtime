# v0.23.0-preview — Python plugins (polyglot plugins pillar)

Tactical plan for v0.23 — let partners drop a Python MCP server under `/var/lib/vais/plugins/` and have the runtime spawn, supervise, and wire its tools into the declarative agent path without rebuilding the .NET runtime. Grounded in the spike + findings + example: [`actor-agents-oss-polyglot-plugins-{spike,findings,example}.md`](./actor-agents-oss-polyglot-plugins-spike.md). Parallel shape to [`actor-agents-oss-v0.18-plugin-model-pillar.md`](./actor-agents-oss-v0.18-plugin-model-pillar.md). Created 2026-04-24.

---

## Scope

**MVP boundary locked 2026-04-24** via the findings-doc synthesis (user confirmed "lock the pillar plan"). 12 decisions:

1. **New package** `Vais.Agents.Runtime.Plugins.Python` (library-layer, `IsPackable=true`). Holds the plugin loader, subprocess supervisor, and MCP-client wiring. Depends on `Vais.Agents.Abstractions` + `Vais.Agents.Protocols.Mcp` (existing) + `Tomlyn` (for `pyproject.toml`) + `YamlDotNet` (already in use elsewhere). **No Orleans dependency.**
2. **Plugin boundary is tool-level only.** Python plugins contribute tools via an MCP server; they do not implement `IAiAgent`. Agent-level polyglot stays on A2A (v0.20/v0.21).
3. **Two-level descriptor**: `plugin.yaml` for runtime dispatch (language-agnostic) + `pyproject.toml` `[tool.vais.plugin]` block for Python-side metadata + `[project.entry-points."vais.plugins"]` for discovery. ABI match: major.minor (reuses `VaisRuntimeAbi.CurrentVersion`).
4. **MCP stdio as the only protocol.** Reuses `Vais.Agents.Protocols.Mcp` client; no new protocol design. Streamable HTTP transport is a future option if multi-silo sharing becomes a requirement.
5. **Silo-scoped subprocess**: one `Process.Start`-launched Python subprocess per plugin per silo. All grains on the silo share the plugin's tools via the existing `IToolRegistry` path. Not per-grain (too expensive).
6. **Supervision via `IPythonPluginHost : IHostedService`** — spawns at startup, health-probes via MCP initialize handshake, restarts crashed subprocesses with exponential backoff (capped at 3 attempts before marking unavailable).
7. **Pre-baked `uv venv --relocatable`** is the canonical packaging shape. OCI-layer overlay at `/var/lib/vais/plugins/<name>/`. Runtime-side `uv sync --frozen --offline` is an opt-in dev fallback (off by default).
8. **Plugin tools reach agents via the existing `mcp:<name>` source prefix** in `AgentManifest.Tools[].Source`. **No manifest schema change.** `AggregatingToolRegistry` is unchanged.
9. **Six new URNs** in a new `PythonPluginUrns` static class: `python-plugin-load-failed`, `python-plugin-abi-mismatch`, `python-plugin-exited`, `python-plugin-unavailable`, `python-plugin-handshake-timeout`, `python-plugin-ambiguous-folder` (folder claimed by both Python and .NET loaders).
10. **Directory layout co-exists with v0.18 .NET plugins.** Same `/var/lib/vais/plugins/` root. Loaders disambiguate by presence of `plugin.yaml` with `runtime: python` (Python loader) vs. a `[VaisPluginAttribute]`-tagged DLL (existing .NET loader). Folders one loader doesn't claim are skipped by that loader; the other loader gets a chance.
11. **Linux containers are production target.** Windows host supported for dev-loop only; `uv venv --relocatable` edge cases documented.
12. **Trusted-plugin security posture**, unchanged from v0.18. Python subprocess runs as the same UID as the runtime; has full network + filesystem access within the pod. Process boundary is a **reliability** boundary, not a security boundary.

### Four open questions resolved (beyond the findings)

The example doc surfaced three runtime-integration questions not answered by the findings. Locking them now so PR 2 can proceed; revise in-flight if blockers emerge.

| # | Question | Decision | Rationale |
|---|---|---|---|
| 13 | **Secret propagation** | Pass via **subprocess environment variables** (`Process.StartInfo.Environment`). Plugin authors declare `[tool.vais.plugin].secrets` as a name → `ISecretResolver`-ref map; runtime resolves each and sets the matching env var on the subprocess. | Consistent with decision 12 (trusted-plugin posture; process boundary = reliability, not security). Avoids a VAIS-specific MCP protocol extension — preserves "no new protocol" (decision 4). Env vars visible in `/proc/<pid>/environ` to same-UID processes, which already have full pod access; acceptable for trusted scope. |
| 14 | **Log correlation** | Runtime captures subprocess **stdout/stderr**, re-emits through `ILogger` with a `plugin={name}` structured scope. Defer OTEL trace-context propagation to v0.23.x polish. | Simplest viable; structured scope is enough for grep/filter. Trace-context over MCP requires custom transport headers — out of scope for v1. |
| 15 | **Handshake timeout semantics** | `plugin.yaml`'s `handshakeTimeoutSeconds` (default 5s) covers the window from spawn to MCP `initialize` response + first `tools/list` response. Exceed → `python-plugin-handshake-timeout` URN; subprocess SIGKILLed; plugin skipped; runtime continues. | Matches v0.18's 5s per-plugin WARN threshold; provides a deterministic failure mode. |
| 16 | **`[tool.vais.plugin].tools` authority** | MCP `tools/list` is **authoritative**. `[tool.vais.plugin].tools` is a **verification-only assertion**. Mismatch: (a) tool in list but not in pyproject → accepted silently; (b) tool in pyproject but not in list → WARN log `python-plugin-tool-mismatch` (no URN — not a failure). | The `tools` field is a static hint for tooling (IDE autocomplete, CI lint); runtime source of truth is always what the server returns. |

### Explicitly deferred to post-v0.23

- **Agent-level Python plugins** — a Python type replacing `IAiAgent`. A2A covers it.
- **Untrusted code sandbox (Track B)** — separate spike `actor-agents-oss-code-sandbox-spike.md`. Different threat model.
- **Non-Python plugin runtimes** (Node, Go, Rust). MCP has SDKs; extending is a follow-up pillar per language.
- **In-process Python embedding** (pythonnet, CSnakes, Pyodide). Disqualified in findings Q2.
- **Token-streaming tool output**. MCP progress notifications cover periodic updates; true token-stream is agent-level.
- **Python plugin hot-reload**. v0.22 hot-reload is .NET-only; Python subprocess restart is mechanically similar but adds test surface — defer to v0.23.x.
- **`/v1/plugins` HTTP endpoint** listing loaded Python plugins. Startup log covers v0.23; API is v0.23.x polish.
- **OTEL trace-context propagation** to/from Python plugins. Log correlation covers v0.23; trace correlation is polish.
- **`uv sync` at runtime as production path.** Dev fallback only; production requires pre-baked `.venv`.
- **Multi-instance plugin scaling** (N subprocesses per plugin for throughput). Single subprocess per plugin per silo in v1; if throughput becomes a concern, Streamable HTTP transport unlocks an external scaled deployment.

---

## Design questions — resolved

Full evidence in [`actor-agents-oss-polyglot-plugins-findings.md`](./actor-agents-oss-polyglot-plugins-findings.md). Summary:

| # | Question | Decision |
|---|---|---|
| 1 | What does Track A add beyond A2A? | Deployment convenience + localhost stdio latency for tool-level code |
| 2 | In-process interop options | All disqualified (multi-interpreter + ecosystem gaps); subprocess only |
| 3 | Environment + packaging | `uv` + `--relocatable` venv pre-baked into OCI layer |
| 4 | Protocol | MCP stdio, reusing `Vais.Agents.Protocols.Mcp` |
| 5 | Orleans integration | Silo-scoped `IHostedService`; grains unchanged |
| 6 | Sandbox (Track B) | Separate spike; out of scope here |
| 7 | ABI descriptor | `plugin.yaml` + `pyproject.toml` `[tool.vais.plugin]` |
| 8 | Security posture | Trusted; process boundary = reliability |
| 9 | Manifest extension (Track C) | None — `source: mcp:<name>` already works |
| 10 | Phasing | One pillar (Track A + C); Track B separate |
| 13 | Secret propagation | MCP `initialize` params |
| 14 | Log correlation | stdout/stderr piped through `ILogger` with `plugin=` scope |
| 15 | Handshake timeout | `handshakeTimeoutSeconds` in plugin.yaml; default 5s; SIGKILL on exceed |
| 16 | Tools manifest authority | `tools/list` authoritative; pyproject `tools` is verification-only |

---

## Proposed PR shape

Four-PR sequence inside v0.23. Each independently shippable.

### PR 1 — Descriptor types + loader (no subprocess spawn)

- [ ] New project `src/Vais.Agents.Runtime.Plugins.Python/` — `Microsoft.NET.Sdk`, `IsPackable=true`, `PublicAPI` analyzer on, `Directory.Build.props`-inherited warnings.
- [ ] `PackageReference`s: `Tomlyn` (latest, for pyproject.toml), `YamlDotNet` (existing), `Vais.Agents.Abstractions`, `Vais.Agents.Protocols.Mcp`.
- [ ] Public types:
  ```csharp
  public sealed record PythonPluginDescriptor(
      string Name,
      string PluginDirectory,
      string InterpreterPath,        // absolute, resolved from plugin.yaml
      string EntrypointPath,         // absolute, resolved from plugin.yaml
      string TargetApiVersion,
      int HandshakeTimeoutSeconds,
      PythonRestartPolicy RestartPolicy,
      IReadOnlyList<string> DeclaredTools,
      IReadOnlyDictionary<string, string> SecretRefs);

  public enum PythonRestartPolicy { Never, ExponentialBackoff }

  public static class PythonPluginUrns
  {
      public const string LoadFailed = "urn:vais-agents:python-plugin-load-failed";
      public const string AbiMismatch = "urn:vais-agents:python-plugin-abi-mismatch";
      public const string Exited = "urn:vais-agents:python-plugin-exited";
      public const string Unavailable = "urn:vais-agents:python-plugin-unavailable";
      public const string HandshakeTimeout = "urn:vais-agents:python-plugin-handshake-timeout";
      public const string AmbiguousFolder = "urn:vais-agents:python-plugin-ambiguous-folder";
  }
  ```
- [ ] Internal `PluginYamlDeserializer` + `PyprojectTomlReader` — parse raw descriptors into `PythonPluginDescriptor`. ABI check against `VaisRuntimeAbi.CurrentVersion` (existing, shared with .NET loader).
- [ ] Internal `PythonPluginScanner` — enumerates `/var/lib/vais/plugins/*/plugin.yaml`, filters `runtime: python`, yields `PythonPluginDescriptor`. Folders without `plugin.yaml` skipped silently (leaves them for the .NET loader).
- [ ] `PythonPluginLoaderOptions`: `PluginsDirectory`, `FallbackUvSync` (bool, default false), `DefaultHandshakeTimeoutSeconds` (int, default 5).
- [ ] Unit tests in `tests/Vais.Agents.Runtime.Plugins.Python.Tests/`:
  - Parse valid `plugin.yaml` + `pyproject.toml` → descriptor populated correctly.
  - ABI mismatch detection (targetApiVersion != runtime's).
  - Missing `plugin.yaml` → folder skipped.
  - `runtime: node` in plugin.yaml → folder skipped (not claimed by Python loader).
  - Malformed YAML / TOML → `python-plugin-load-failed` URN in the result.
  - Interpreter path resolution: relative `.venv/bin/python` → absolute plugin-dir-prefixed.
- [ ] `PublicAPI.Unshipped.txt` for `Vais.Agents.Runtime.Plugins.Python`: `PythonPluginDescriptor`, `PythonRestartPolicy`, `PythonPluginUrns`, `PythonPluginLoaderOptions`.
- [ ] Full solution builds 0 warnings / 0 errors; all existing tests green + new loader tests green.

### PR 2 — Subprocess supervision + MCP client handshake

- [ ] `IPythonPluginHost : IHostedService` in `Runtime.Plugins.Python`:
  ```csharp
  public interface IPythonPluginHost
  {
      IReadOnlyCollection<LoadedPythonPlugin> LoadedPlugins { get; }
  }

  public sealed record LoadedPythonPlugin(
      PythonPluginDescriptor Descriptor,
      PythonPluginStatus Status,
      int? ProcessId,
      IMcpClient? McpClient);

  public enum PythonPluginStatus { Loading, Ready, Restarting, Unavailable }
  ```
- [ ] Internal `PythonSubprocessSupervisor` — per-plugin state machine:
  - **Loading**: spawn `Process.Start(interpreterPath, entrypointPath)` with `WorkingDirectory = plugin dir`, `RedirectStandardInput/Output/Error = true`.
  - Wire process stdio → MCP client (new `StdioClientTransport` in `Vais.Agents.Protocols.Mcp` if not already present — shared with v0.7 outbound path).
  - Before spawn: for each entry in `descriptor.SecretRefs`, resolve via `ISecretResolver` and set on `ProcessStartInfo.Environment[name] = resolvedValue`. Log at Debug that secrets were injected (count only; never the values).
  - Send MCP `initialize` (standard fields only — no VAIS extensions).
  - Wait for `initialize` response + `tools/list` response within `handshakeTimeoutSeconds`. Exceed → SIGKILL + `python-plugin-handshake-timeout`.
  - Verify `tools/list` contains all `descriptor.DeclaredTools`; missing ones → WARN log `python-plugin-tool-mismatch` (not a failure).
  - **Ready**: state = Ready; fire `Loaded` event.
  - **Restarting**: on process exit, exponential backoff (1s, 2s, 4s, capped at 3 attempts); re-spawn; re-handshake. Final attempt fail → **Unavailable**.
  - **Unavailable**: any `tools/call` against this plugin's tools returns `python-plugin-unavailable` URN.
- [ ] Internal `SubprocessLogForwarder` — reads subprocess stderr + stdout (stdout non-MCP frames only, since MCP owns framed stdout), re-emits via `ILogger` with `BeginScope("plugin", descriptor.Name)`. Stderr → Information level; unexpected stdout → Warning level.
- [ ] `IHostedService.StartAsync`: load all descriptors, spawn subprocesses with bounded parallelism (`Math.Min(Environment.ProcessorCount, 4)` to avoid fork storms on large hosts with small cgroup CPU limits), await all handshakes. Failed plugins logged + marked Unavailable; `StartAsync` returns successfully (runtime ships with some plugins unavailable rather than refusing to start).
- [ ] `IHostedService.StopAsync`: send MCP `shutdown`, wait up to `drainSeconds: 5`, then SIGINT, then SIGKILL after `2s` more.
- [ ] DI extension: `AddPythonPlugins(this IServiceCollection, PythonPluginLoaderOptions)`. Registers `IPythonPluginHost` (singleton, as `IHostedService`) + wires the supervisor.
- [ ] Unit tests — use a minimal .NET-based mock MCP server (spawn a `dotnet test` child process that speaks MCP stdio):
  - Golden path: spawn mock, handshake succeeds, `LoadedPlugins` reports Ready.
  - Handshake timeout: mock sleeps through the 5s budget → Unavailable + URN logged.
  - Mid-session crash: mock exits unexpectedly → Restarting → Ready after second spawn.
  - Restart exhaustion: mock always exits → Unavailable after 3 attempts.
  - Log forwarding: mock writes to stderr → `ILogger` captures with scope.
- [ ] Full solution builds 0 warnings / 0 errors; all tests green.

### PR 3 — IToolSource bridge + composition root + Python integration tests

- [ ] `LoadedPythonPlugin` exposes `IToolSource` — registers the MCP client as a `McpToolSource` in the `IToolRegistry` with source key `mcp:{descriptor.Name}`. This is the one line that makes Python tools reachable via existing agent machinery.
- [ ] `Vais.Agents.Runtime.Host.CompositionRoot` updates:
  ```csharp
  if (!string.IsNullOrWhiteSpace(options.PluginsDirectory) && Directory.Exists(options.PluginsDirectory))
  {
      services.AddAgentPlugins(options.PluginsDirectory, ...);       // v0.18 .NET loader
      services.AddPythonPlugins(new PythonPluginLoaderOptions        // NEW v0.23
      {
          PluginsDirectory = options.PluginsDirectory,
          DefaultHandshakeTimeoutSeconds = 5,
      });
  }
  ```
- [ ] Env var: no new var. Reuses existing `VAIS_PLUGINS_DIRECTORY`. Descriptor `runtime:` field disambiguates loader.
- [ ] Integration test project: add a Python-dependent test project `tests/Vais.Agents.Runtime.Host.PythonPlugins.Tests/` with `Traits = ["RequiresPython"]` on every fact. Tests gated by env var `VAIS_RUN_PYTHON_PLUGIN_TESTS=1` to keep CI green on environments without Python. Bundled Python fixture plugin ships in `tests/Fixtures/python-plugin-fixture/` (pre-baked `.venv` not committed — produced by a pre-test `uv sync` step documented in the project README).
- [ ] Integration tests (all Golden-path + Failure-mode from the example doc's test blueprint):
  - **Golden path**: runtime starts with fixture plugin; `IPythonPluginHost.LoadedPlugins` has one entry with Status=Ready; `IToolRegistry.Tools` contains three tools with source `mcp:fixture`; mocked-LLM agent invokes each → expected result.
  - **Missing venv**: fixture folder without `.venv/` → LoadFailed URN; runtime continues.
  - **ABI mismatch**: fixture's pyproject targets `"99.99"` → AbiMismatch URN; plugin skipped.
  - **Handshake timeout**: fixture entrypoint sleeps 10s before responding to `initialize` → HandshakeTimeout URN after 5s; SIGKILLed.
  - **Subprocess exit mid-invocation**: trigger fixture to `sys.exit(1)` on a specific tool call → Restarting → Ready; next tool call succeeds. After 3 forced exits → Unavailable → `tools/call` returns `python-plugin-unavailable`.
  - **Tools-list mismatch**: fixture's pyproject declares a tool the server doesn't expose → WARN logged; declared-but-missing silently dropped.
- [ ] Unit tests for the composition-root wiring (no Python needed):
  - `AddPythonPlugins` is idempotent with `AddAgentPlugins` (both can coexist).
  - Coexistence: folder with both `plugin.yaml` (runtime: python) AND a `[VaisPluginAttribute]`-tagged DLL → both loaders emit `python-plugin-ambiguous-folder` / the .NET equivalent; **neither** plugin loads; operator must resolve the collision. Matches v0.18's `plugin-handler-collision` "refuse both" precedent. Sixth URN added to the catalog.
- [ ] Full solution builds 0 warnings / 0 errors; CI green on default (Python tests skipped); opt-in Python test run green on author's machine.

### PR 4 — Sample + docs + PublicAPI freeze + tag

- [ ] New sample `samples/PluginAgentResearchPlanner/` — full implementation of the [example doc](./actor-agents-oss-polyglot-plugins-example.md):
  - `plugin.yaml`, `pyproject.toml`, `uv.lock`, `Dockerfile.overlay`
  - `src/research_planner/{__init__,server,planner,schemas}.py`
  - `README.md` — build + package + deploy walkthrough
  - Planner logic can be heuristic (word-split, template-based) to keep the sample hermetic — no LLM calls inside the Python.
  - **End-to-end validation (PR 4 acceptance)**: build `ghcr.io/vais-agents/runtime:0.23.0-preview` base + overlay image, run the container, confirm plugin loads and tools invoke correctly — proves the "runtime base stays Python-free; plugins ship their own interpreter" story in full. Smoke script documented in the sample README.
- [ ] New concept doc `docs/concepts/polyglot-plugins.md`:
  - Scope (tool-level, not agent-level); A2A as the agent-level alternative
  - Descriptor format (plugin.yaml + pyproject.toml)
  - Subprocess lifecycle + supervision
  - Security posture (trusted; process boundary = reliability)
  - URN catalogue
  - Coexistence with v0.18 .NET plugins
  - Not-in-scope explicitly (sandbox, agent-level, in-process Python, hot-reload)
- [ ] New guide `docs/guides/package-a-python-plugin.md` — mirror of `package-an-agent-as-a-plugin.md` but for Python:
  - Scaffold with `uv init`
  - Write the MCP server
  - `uv sync` + `uv venv --relocatable`
  - `Dockerfile.overlay`
  - Verify startup log, invoke via declarative agent
  - Troubleshooting: handshake timeout, ABI mismatch, missing .venv
- [ ] `docs/reference/problem-details-urns.md` — five new URNs in a v0.23 section.
- [ ] `docs/reference/runtime-configuration.md` — Python plugin sub-section under Plugin loader, references the two descriptors.
- [ ] `docs/roadmap/deferred-backlog.md` — §3 Plugins & hosting: strike through non-.NET plugins entry, add **PARTIALLY SHIPPED v0.23** (Python only — Node/Go/Rust remain deferred).
- [ ] `PublicAPI.Shipped.txt` promotion for `Vais.Agents.Runtime.Plugins.Python`.
- [ ] Milestone entry appended to `plans/actor-agents-oss-milestone-log.md`.
- [ ] Tag `v0.23.0-preview`.

**Sizing:** PR 1 ≈ 2 days, PR 2 ≈ 3-4 days, PR 3 ≈ 2-3 days, PR 4 ≈ 1-2 days. **Total 8-11 working days** (~2 weeks).

---

## Acceptance

Pillar is done when:

- [ ] A `research-planner` sample plugin, packaged per the example doc, loads at runtime startup and reports Status=Ready.
- [ ] A declarative agent manifest referencing `source: mcp:research-planner` invokes Python-backed tools successfully via `vais invoke`.
- [ ] All six integration-test scenarios from the example's test blueprint pass (golden path + five failure modes).
- [ ] Startup log clearly indicates which plugins loaded, which were skipped, and why (ABI mismatch, missing venv, handshake timeout).
- [ ] `VAIS_PLUGINS_DIRECTORY` points at a mixed directory; both .NET (v0.18) and Python (v0.23) plugins load correctly in the same pod.
- [ ] Subprocess crash → automatic restart → resumed operation within 8s (1s + 2s + 4s backoff ≤ 7s + spawn time).
- [ ] Subprocess stdout/stderr appears in runtime logs with `plugin={name}` scope.
- [ ] Secret refs declared in `pyproject.toml` arrive at the Python plugin as environment variables on the spawned subprocess; values never appear in runtime logs.
- [ ] Full solution 0 warnings / 0 errors; every test project green; Python-gated tests runnable via `VAIS_RUN_PYTHON_PLUGIN_TESTS=1` on a dev machine with Python 3.13 + uv installed.
- [ ] Docs reviewed; cross-links intact from `index.md` / `concepts/runtime-plugins.md` / `runtime-configuration.md` / `problem-details-urns.md`.
- [ ] Tag `v0.23.0-preview` created.

---

## Composition-root sketch

```csharp
public static void ConfigureServices(IServiceCollection services, RuntimeOptions options)
{
    // ... v0.17 declarative + v0.18 .NET plugin wiring unchanged ...

    if (!string.IsNullOrWhiteSpace(options.PluginsDirectory) && Directory.Exists(options.PluginsDirectory))
    {
        // v0.18 — .NET plugins (existing)
        services.AddAgentPlugins(options.PluginsDirectory, new PluginLoaderOptions
        {
            ReloadPolicy = options.ReloadPolicy ?? ReloadPolicy.Disabled,
        });

        // v0.23 — Python plugins (NEW)
        services.AddPythonPlugins(new PythonPluginLoaderOptions
        {
            PluginsDirectory = options.PluginsDirectory,
            DefaultHandshakeTimeoutSeconds = 5,
            FallbackUvSync = options.AllowRuntimeUvSync, // dev fallback
        });
    }
}
```

Consumer opt-in (one extra block beyond v0.22):

```csharp
builder.Services.AddPythonPlugins(new PythonPluginLoaderOptions
{
    PluginsDirectory = "/var/lib/vais/plugins",
    DefaultHandshakeTimeoutSeconds = 5,
});
```

---

## Subprocess supervisor — state machine sketch

```
   [Loading]
      │  spawn Process.Start
      │  MCP initialize (scoped secrets)
      │  tools/list
      │
      │──── ok ────▶ [Ready] ──── grain invokes tool ───┐
      │                                                   │
      │                                                   │  process exits
      │                                                   ▼
      │                                              [Restarting]
      │                                                   │
      │                                                   │  backoff 1s/2s/4s
      │                                                   │  ≤ 3 attempts
      │                                                   │
      │                                             attempt ok ──▶ [Ready]
      │                                             attempts exceeded
      │                                                   │
      └──── handshake timeout ────────────────────▶ [Unavailable]
                                                          │
                                            any tool call: URN
                                            python-plugin-unavailable
```

---

## Risks + mitigations

- **Python 3.13 not available in the runtime image.** The runtime's base image (`ghcr.io/vais-agents/runtime:0.23.0-preview`) must ship with Python 3.13 (or whatever `plugin.yaml` interpreter path expects) installed, OR each plugin must ship its own `python-build-standalone` in its `.venv`. **Mitigation**: python-build-standalone pre-bake is the documented path; base runtime image stays Python-free. Plugins are self-contained.
- **Subprocess fork storms at startup with many plugins.** 20 plugins × cold Python start could saturate a pod's CPU for seconds. **Mitigation**: bounded-parallel startup (`Environment.ProcessorCount` cap); monitor with per-plugin OTEL spans in v0.23.x.
- **MCP SDK CVE drift.** CVE-2026-30623 (April 15 2026) landed on the SDK's stdio client. **Mitigation**: pin the plugin-side MCP dependency via `uv.lock` at audit-friendly minor versions; document the CVE in the security section of the concept doc; runtime-side uses its own .NET MCP client, not the Python SDK.
- **Handshake budget too tight for large plugins.** 5s default may be too short for a plugin with many imports (pandas, torch). **Mitigation**: `handshakeTimeoutSeconds` is per-plugin override; plugins with heavy imports declare longer budgets.
- **Restart-backoff masks real bugs.** Crashed-on-start plugins get silently restarted 3 times before surfacing. **Mitigation**: emit `python-plugin-exited` URN at WARN on every exit; only mark Unavailable after 3. Operators monitoring log streams see the repeated warnings.
- **Env-var secrets visible to same-UID processes.** Any process with UID match to the runtime can read `/proc/<pid>/environ`. **Mitigation**: acceptable for trusted-plugin scope (decision 12); no untrusted code runs in the pod by design. If a future trust-separation requirement emerges, the `descriptor.SecretRefs` hook can be backed by a different delivery mechanism without changing the plugin-author surface.
- **Tools discoverable at runtime aren't in the manifest validator's knowledge.** `vais apply -f agent.yaml` before plugins load can't verify `source: mcp:foo` resolves. **Mitigation**: matches v0.18 "apply accepts, invoke fails late" precedent; document the behaviour.
- **Windows dev loop friction.** `uv venv --relocatable` has known Windows edge cases. **Mitigation**: explicit "Linux-first production; Windows dev supported with caveats" statement in the guide; dev-loop troubleshooting section.
- **Python plugin pulling in a different `Vais.Agents.Abstractions` schema version.** Python side has no type-identity check (JSON-Schema serialization). **Mitigation**: ABI match on `targetApiVersion` at load time is the gate; plugins that drift from the runtime's MCP message shapes fail at first tool invocation with a clear error.

---

## Progress log

- 2026-04-24 — Spike created (`actor-agents-oss-polyglot-plugins-spike.md`). Three tracks: A trusted Python plugin, B untrusted code sandbox, C declarative Python tool.
- 2026-04-24 — Findings synthesised from 6-stream research pass (pythonnet, Pyodide, sandbox landscape, uv, SGR Agent Core, internal codebase). Scope narrowed to tool-level Python-as-MCP-server; Track B deferred to separate spike; Track C is free consequence of Track A.
- 2026-04-24 — Example doc (`actor-agents-oss-polyglot-plugins-example.md`) authored as test reference specification.
- 2026-04-24 — Pillar plan created. 12 scope decisions + 4 runtime-integration decisions (secret propagation via MCP initialize, log correlation via ILogger scope, 5s handshake default, pyproject.tools as verification-only). Four-PR sequence: descriptor types + loader → subprocess supervision + MCP handshake → IToolSource bridge + composition root + integration tests → sample + docs + tag. ~8-11 working days.
