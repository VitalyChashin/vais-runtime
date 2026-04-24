# Polyglot plugins — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-polyglot-plugins-spike.md`](./actor-agents-oss-polyglot-plugins-spike.md). Answers Q1–Q10 with evidence from a 6-stream research pass (pythonnet/CSnakes, Pyodide, sandbox landscape, uv, SGR Agent Core, internal codebase).

Created 2026-04-24. **Status**: complete.

---

## Scope narrowing up front

The spike opened three tracks (A — trusted Python plugin; B — untrusted code sandbox; C — declarative Python tool). The evidence forces a meaningful narrowing before the question-by-question answers:

1. **In-process Python embedding is disqualified for a multi-tenant plugin host.** Every in-process option (Python.NET, CSnakes, Pyodide via Wasmtime, CPython-WASI) fails either the isolation test or the ecosystem test — details in Q2. This collapses Track A's protocol choice to out-of-process only.

2. **The out-of-process protocol is already in the codebase.** MCP stdio (`Vais.Agents.Protocols.Mcp` + `Vais.Agents.Protocols.Mcp.Server`) gives us framed JSON-RPC over stdin/stdout, with `McpToolSource` already wiring an external MCP server's tools into `IToolRegistry`. A Python plugin running as an MCP server needs **zero new protocol design** — it reuses stable v0.7 infrastructure.

3. **The realistic plugin boundary is tool-level, not agent-level.** v0.18's .NET plugin model replaces an entire `IAiAgent` (streaming, history, prompt, tool loop). Reproducing that boundary across a subprocess requires marshalling `IAsyncEnumerable<CompletionUpdate>`, `IReadOnlyList<ChatTurn>`, `AgentContext`, and the full `IAiAgent` contract over stdio — substantial protocol design for a capability that **A2A cross-runtime refs already cover** (v0.20/v0.21). The honest scope for non-.NET plugins in the first pass is "Python contributes tools to a declarative agent," not "Python replaces the agent."

4. **Track B (sandbox) is a separate concern.** Different threat model (untrusted vs. trusted), different lifecycle (ephemeral vs. persistent), different ecosystem (Deno/gVisor/Firecracker vs. MCP/uv). Keeping it in this doc muddles both decisions. It is deferred to a dedicated `actor-agents-oss-code-sandbox-spike.md` referenced in §"Out of scope for this pass."

5. **Track C is a free consequence of Track A.** If Python plugins expose their capabilities as MCP tools, the existing `IToolSource` + `ToolRef(Name, Source)` surface handles them without new abstractions. Track C does not need a separate pillar.

The landing verdict is structured around this narrowing: the first pass ships Python-as-MCP-server with uv-based packaging, stdio lifecycle management, and reuse of the v0.22 hot-reload infrastructure. Full-agent Python replacement and untrusted code sandbox are deferred with explicit rationale.

---

## Q1 — What capability does Track A add beyond A2A?

### Evidence

- A2A cross-runtime refs (v0.20 Pillar E + v0.21) give every polyglot agent a deployment path: the agent runs in its own process/pod/runtime, exposes A2A endpoints, and is referenced by other agents over HTTP. This covers *agent-level* polyglot fully — Python agents, Node agents, anything with an A2A server is reachable.
- Gaps A2A does **not** close:
  - **Deployment convenience.** An A2A agent is its own deployable: its own image, its own pod, its own scaling knobs, its own health checks. A partner who wants to ship "a Python planning tool that participates in my .NET agent's loop" has to build and operate a full agent deployment for a function-level contribution.
  - **Latency of localhost stdio vs. network.** A2A's network round-trip (even within the same cluster) is an order of magnitude slower than stdio IPC between colocated processes. For tools invoked many times per agent turn (planners, structured-output helpers), the difference compounds.
  - **Packaging-as-plugin.** v0.18 established a mental model where a plugin is a directory under `/var/lib/vais/plugins/`, loaded at startup, hot-reloadable in v0.22. Partners want the same operational story for Python code, not "stand up a new service."
- What A2A already covers and Track A should not try to replace:
  - Full agent-level conversational loops (streaming, history, prompts, guardrails) — these are agent-shaped, not tool-shaped.
  - Cross-organizational boundaries — A2A's HTTP+auth layer is the right boundary for independent deployments.

### Decision (Q1): **Track A's value is deployment convenience for tool-shaped Python code; it does not attempt to replicate A2A's agent-level boundary.**

The formal scope: a non-.NET plugin **contributes tools and/or capabilities via an MCP-like tool protocol**; it does not implement `IAiAgent`. Partners who want a Python *agent* use A2A. Partners who want a Python *tool bundled with a .NET agent* use a non-.NET plugin.

Naming: "polyglot plugin" is retained as the external name. "Python plugin" is the v1 concrete shape. Other languages (Node, Go) fall out naturally once the protocol + packaging are settled — MCP has cross-language SDKs already.

---

## Q2 — In-process Python/.NET interop options

### Evidence

**Python.NET (pythonnet).** Current stable 3.0.5 (Dec 2024); 3.1.0-rc0 (Feb 2026) adds Python 3.14.
- Hard single-interpreter-per-process limit. Maintainer statement in discussion #2397: *"This is not possible, Python itself doesn't support this. You'll have to spin up individual processes for the scripts."* PEP 684 (per-interpreter GIL) is not exposed by pythonnet.
- GIL model is thread-affine. Acquiring `Py.GIL()` across an `await` is documented as unsafe — continuations resume on arbitrary thread pool threads, corrupting GIL state. Orleans grains are cooperatively scheduled; running a long GIL-holding call on a grain's dispatch thread blocks other grains on that silo.
- Production evidence for long-running async scenarios is thin. Open issues around async + GIL (#109, #787, #964, #1587), memory leaks in repeated `Initialize`/`Shutdown` cycles (#499), and .NET object refcount leaks (#1734). The community workaround for leaks is "run Python in a subprocess" — hostile to the in-process premise.

**CSnakes.** Newer (tonybaloney/CSnakes), actively maintained, .NET 8/9 source-generator approach; Python 3.9–3.13; supports free-threaded 3.13t explicitly; numpy buffer-protocol zero-copy; hot-reload of Python source. Still a single-interpreter-per-process constraint — same underlying CPython embedding model, same multi-tenancy blocker.

**Pyodide via Wasmtime.** Pyodide is Emscripten-bound. Maintainer statement (discussions #5145, 2024): *"Pyodide is built by the Emscripten toolchain and can only run in a browser or Node.js."* Issue #558 (`wont-implement`) confirms no plan to support non-browser WASM runtimes. LangChain Sandbox / Pydantic `mcp-run-python` pattern is to shell out to Deno or Node.js hosting Pyodide — which is a subprocess, not in-process.

**CPython-WASI + wasmtime-dotnet.** CPython 3.13+ has Tier-2 WASI support (PEP 816). VMware Labs publishes prebuilt `python.wasm` for `wasm32-wasi`.
- Disqualifiers: (a) no scientific wheels for `wasm32-wasi` — numpy/pandas/torch don't run; (b) wasmtime-dotnet lags upstream Wasmtime by ~1 version and had a 12-month gap between v22 and v34 (2024→2025); (c) .NET 10 dropped WASI as an official target; (d) WASI preview 2 / component model not production-ready in wasmtime-dotnet.

**IronPython 3.** Pure-.NET Python reimplementation, no C-extension compatibility. Disqualified for any workload touching numpy/pandas/pydantic-core.

### Decision (Q2): **All in-process options disqualified for a multi-tenant plugin host. Protocol choice is out-of-process stdio.**

Rationale:
- Multi-tenancy requires per-plugin environment isolation. No in-process option supports multiple CPython interpreters per .NET process; subinterpreters (PEP 684) are not exposed by any wrapper.
- Scientific-stack support requires CPython-native; WASI-CPython has no wheels for it.
- Orleans async scheduling interacts badly with CPython's GIL.
- The codebase already has stdio MCP wired end-to-end — no new protocol needed.

The remaining candidate list (stdio subprocess / gRPC sidecar) collapses to **stdio MCP** on the basis of reuse: `StdioAgentServerHost` + `McpToolSource` + `McpBackedTool` implement the full path already.

---

## Q3 — Python environment isolation and packaging

### Evidence

- **uv (Astral) as the Python environment tool.** Current 0.11.7 (April 2026), production-grade, single static binary per platform. Venv creation + install of 20 packages: **~1.5s cold, ~200ms warm** (bracketed by published 23-package benchmarks). `uv.lock` is SHA-256 hashed, cross-platform universal. `UV_OFFLINE=1` + `UV_CACHE_DIR` lets us install entirely from a pre-populated wheel cache — no network at silo start.
- **`uv venv --relocatable`** (PR #5515) rewrites activation scripts + entry-point shebangs to use relative paths — fixes the canonical "pre-baked venv breaks under different root path" problem that would bite OCI-layer packaging. **Windows caveat (verified 2026-04-24):** issue #15751 remains open tracking remaining portability gaps; `--relocatable` cannot be set in `pyproject.toml` or passed to `uv sync` (must be set at `uv venv` creation time). Linux-container production scenarios work cleanly; Windows dev-loop scenarios may hit edge cases around `.venv` re-creation and file-locking (issues #13986, #17678).
- **Python-version management.** uv replaces pyenv; `uv python install 3.13` downloads python-build-standalone tarballs. These are relocatable, libc-aware (musl + glibc variants), and suitable for pre-baked OCI layers.
- **Pre-baked OCI layer** is the safer architectural choice over runtime bootstrap. The plugin's build process produces `plugins/my-tool/python/` with `python-build-standalone/` + `.venv/` + source; the runtime's job at startup is to `exec` the interpreter, not to resolve dependencies.
- **Native extensions.** uv + python-build-standalone supports the full CPython native-extension story (numpy, pandas, torch, pydantic-core). No limitations beyond matching libc flavor between build and runtime images.

### Decision (Q3): **Pre-baked venv via uv with `--relocatable`; OCI layer is the canonical packaging shape. Runtime-side `uv sync` is an opt-in dev fallback. Linux containers are the production target; Windows host is dev-only.**

Directory structure (per-plugin):
```
/var/lib/vais/plugins/
├── weather-python/
│   ├── plugin.yaml                      # runtime descriptor (see Q7)
│   ├── pyproject.toml                   # Python descriptor + [tool.vais.plugin]
│   ├── uv.lock                          # deterministic, hash-pinned
│   ├── .venv/                           # pre-baked by uv venv --relocatable
│   │   ├── bin/python                   # relative-path shebangs
│   │   └── lib/python3.13/site-packages/
│   └── src/my_plugin/
│       ├── __init__.py
│       └── server.py                    # MCP server entrypoint
```

Build-time command (plugin author side):
```bash
uv sync --frozen                         # installs into .venv
uv venv --relocatable                    # rewrites shebangs
```

Runtime-side fallback (`VAIS_PLUGINS_PYTHON_BOOTSTRAP=sync`): if the `.venv` is missing but a `uv.lock` is present, run `uv sync --frozen --offline` against a mounted wheel cache. Off by default in production — the OCI-layer shape is primary.

---

## Q4 — Protocol for cross-boundary calls

### Evidence

- **MCP stdio is already in the codebase.** `Vais.Agents.Protocols.Mcp.Server.StdioAgentServerHost` uses the official MCP SDK's `StdioServerTransport`; `Vais.Agents.Protocols.Mcp.McpToolSource` implements `IToolSource` by calling `McpClient.ListToolsAsync()` against a configured server; `McpBackedTool` wraps each MCP tool as an `ITool` with a `Task<string> InvokeAsync(JsonElement args, CT)` signature that matches the .NET tool contract exactly.
- MCP tools natively support: JSON-Schema argument schema, streaming progress notifications (optional), named resources, structured error responses. Streaming token-by-token output from a tool is NOT in MCP's core model — it fits the request/response shape, not agent-level streaming.
- The manifest already supports MCP server references via `AgentManifest.McpServers` (`McpServerRef`). Extending this with a `kind: python` lifecycle-management flag is additive — the existing type shape is reused.
- Alternative protocols (gRPC, HTTP+SSE, named pipes) all require new protocol design, new SDK, new client/server implementations on both sides. None offer a capability MCP lacks for the tool-shaped scope.
- **Python MCP SDK maturity (verified 2026-04-24):** Official `mcp` package on PyPI, maintained by Anthropic, version 1.27.0 (Apr 2, 2026), active release cadence (multiple releases per month through 2025-2026). Still carries a "Development Status :: 4 - Beta" classifier despite the 1.x version number — treat as production-capable but monitor. Python ≥ 3.10 required.
- **Known SDK risk:** CVE-2026-30623 (published April 15, 2026) — command injection in Anthropic's MCP SDK stdio transport. The flaw is in the SDK's stdio **client** when spawning subprocesses from untrusted configuration; our model pins the plugin executable path and arguments from the operator-controlled `plugin.yaml`, not from untrusted input, so we are not directly exposed. Still: pillar plan must require that `plugin.yaml` `entrypoint` values are resolved strictly relative to the plugin directory and never passed through a shell.
- **Known SDK limitation:** stdio transport is documented as unsuitable for concurrent *client* connections (≥20 simultaneous clients → high failure rate per industry reports). This does not affect us — our model is 1:1 (one runtime MCP client, one plugin subprocess per plugin). Multiplexed tool calls inside one stdio channel are handled by JSON-RPC request IDs and work reliably. If we later need multi-silo sharing of a single plugin, we'd switch that specific case to Streamable HTTP (SDK also supports it).

### Decision (Q4): **MCP over stdio. No new protocol.**

Concrete shape:
- The Python plugin ships a minimal MCP server implementation (using the official Python MCP SDK — `mcp` on PyPI).
- The runtime spawns the plugin as a subprocess; stdin/stdout carry JSON-RPC 2.0 framed per the MCP spec.
- Tool discovery uses `tools/list`; invocation uses `tools/call`. Both map directly to `McpToolSource.DiscoverAsync()` and `McpBackedTool.InvokeAsync()`.
- Streaming of tool output is NOT in scope for v1 — tools return their full result as a string/JSON. Tools that need incremental progress use MCP progress notifications (already supported by the SDK).

### Open question (resolved below Q5) — plugin boundary is agent-level or tool-level?

Answered by the scope narrowing: **tool-level only for v1**. The boundary is clean at the tool contract (`ITool.InvokeAsync(JsonElement, CT) → Task<string>`); extending to agent-level requires marshalling streaming + history + prompts across stdio, which MCP does not model and which A2A already solves.

---

## Q5 — Orleans integration shape for Track A

### Evidence

- With the tool-level boundary locked, the Orleans integration question simplifies: the MCP client lives at the **silo scope**, not grain scope. One Python subprocess per plugin per silo; all grains on that silo share it via the existing `IToolRegistry` → `IToolSource` → `McpToolSource` chain.
- Per-grain Python processes (spinning a subprocess on every grain activation) was the expensive option — tens of ms per activation plus interpreter cold start plus package import time. Avoided by tool-level scope.
- The existing `McpToolSource` is already silo-scoped (registered as a singleton by DI). The change is purely in the factory that constructs it: today it configures the MCP client from a `McpServerRef` (user-managed subprocess or remote URL); tomorrow the factory also accepts `"kind": "python-plugin"` descriptors where the runtime is responsible for spawning and supervising the subprocess itself.
- Grain dispatch is unchanged. `StatefulAiAgent` calls `_toolCallDispatcher.DispatchAsync(toolCall, ...)` — no knowledge of Python vs. .NET at the call site.

### Decision (Q5): **Silo-scoped subprocess, supervised by a new `IPythonPluginHost` service. Grains are unchanged.**

New service surface (rough shape, to be finalized in the pillar plan):
```csharp
internal interface IPythonPluginHost : IHostedService
{
    IReadOnlyCollection<LoadedPythonPlugin> LoadedPlugins { get; }
}

internal sealed record LoadedPythonPlugin(
    string PluginName,
    string ExecutablePath,
    Process Process,
    IMcpClient McpClient);
```

Responsibilities:
- At silo startup, enumerate `/var/lib/vais/plugins/*/plugin.yaml` with `runtime: python`. Spawn each as a subprocess (via `Process.Start` wrapping the plugin's pre-baked `.venv/bin/python src/.../server.py`).
- Attach the subprocess's stdin/stdout to an MCP client; register the client as an `IToolSource` in the host DI container.
- On subprocess exit (crash or shutdown), emit a `urn:vais-agents:python-plugin-exited` URN, attempt one automatic restart with exponential backoff, then mark the plugin unavailable. Tools from that plugin surface `503 urn:vais-agents:python-plugin-unavailable` at invoke.
- On `IHostApplicationLifetime.ApplicationStopping`, SIGINT then SIGKILL subprocess after a drain window.

Supervision lives in `IHostedService.StartAsync` / `StopAsync` — familiar lifecycle shape, matches the runtime's existing hosted-service pattern.

---

## Q6 — Track B: sandboxed code execution architecture

### Evidence

- Separate spike required. Evidence gathered in this pass, summarized here for context:
  - Deno subprocess with `--allow-none` + `--v8-flags="--max-old-space-size=N"` is the strongest per-call isolation for JS. Cold start ~50-150ms (anecdotal; no authoritative benchmark).
  - OpenAI Code Interpreter runs on gVisor; E2B on Firecracker microVMs with 80-150ms cold start; Modal/Daytona/Northflank similar.
  - Pyodide sandbox escape (Cyera Cellbreak / CVE-2026-5752, March 2026) means Pyodide alone is not a primary isolation boundary.
  - In-process V8 (ClearScript) terminates the host process on memory-constraint breach — disqualifying for a multi-tenant runtime.
  - Pyodide-in-Deno (LangChain Sandbox, Pydantic mcp-run-python) is the emerging pattern for pure-Python execution; langchain-sandbox was archived Jan 2026, mcp-run-python still active.

### Decision (Q6): **Deferred to a separate spike.** Track B is out of scope for this findings doc.

Rationale: (a) the trust model is opposite (untrusted vs. trusted); (b) the protocol is not MCP (sandbox wants ephemeral eval, not a long-lived tool server); (c) the vendor landscape has its own productized options (E2B, Modal) that a Vais-native implementation must compete with on real value; (d) the cryptographic/CVE exposure requires its own security review.

Tracking: file a `actor-agents-oss-code-sandbox-spike.md` as a new research track. Reuse some infrastructure from Track A (subprocess lifecycle management, uv packaging) if spike outcomes point that way.

---

## Q7 — ABI and versioning for non-.NET plugins

### Evidence

- `pyproject.toml` is the canonical Python project descriptor; PEP 621 reserves `[tool.<name>]` blocks for any project's configuration.
- `[project.entry-points."<group>"]` is the idiomatic Python runtime-discovery mechanism (pytest, Flask, setuptools all use it); `importlib.metadata.entry_points(group="vais.plugins")` is the Python equivalent of scanning for `[VaisPluginAttribute]` in .NET.
- The `.NET VaisPluginAttribute` pattern is specifically: `(targetApiVersion: "0.18", handlers: [...])`. The Python equivalent can be the same two fields.
- A runtime-level `plugin.yaml` sits above `pyproject.toml` and describes the plugin to the runtime loader in a language-agnostic way — the loader checks `runtime: python` before opening `pyproject.toml`.

### Decision (Q7): **Two-level descriptor. `plugin.yaml` for runtime dispatch; `pyproject.toml` with `[tool.vais.plugin]` for Python-side metadata.**

Runtime descriptor `plugin.yaml` (language-agnostic):
```yaml
apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: weather-python
spec:
  runtime: python               # dispatches loader — future: node, wasm
  entrypoint: src/weather/server.py
  python:
    version: "3.13"
    interpreter: .venv/bin/python     # relative to plugin folder
```

Python-side metadata `pyproject.toml` (co-authored with the plugin source):
```toml
[tool.vais.plugin]
targetApiVersion = "0.23"    # matched against VaisRuntimeAbi.CurrentVersion, major.minor
tools = ["get_weather", "forecast_window"]

[project.entry-points."vais.plugins"]
server = "weather.server:main"   # runtime-discoverable entry point
```

ABI match policy follows the existing v0.18 rule: major.minor match during the 0.x line; tightens to semver-major after v1.0. The comparison lives in the Python-plugin loader, which reuses `VaisRuntimeAbi.CurrentVersion`.

Type-identity at the boundary is handled by MCP's JSON-Schema model, not by CLR type sharing — no equivalent to `PluginAssemblyLoadContext.SharedAssemblies` is needed. The schema *of* MCP messages (`tools/list`, `tools/call`, error shapes) is versioned by MCP itself; we follow whatever MCP the SDK supports.

---

## Q8 — Security posture

### Evidence

- Python plugins in v1 are **trusted**, same posture as v0.18 .NET plugins: packaged into the runtime container via overlay image, code-reviewed by operator, no sandbox between plugin process and runtime container.
- Unlike .NET plugins, Python plugins run in a separate PID — this is a *deployment boundary*, not a *security boundary*. Compromised Python plugin can still: (a) make outbound network requests from the runtime pod, (b) read env vars and mounted secrets, (c) exhaust pod memory/CPU until the orchestrator kills the pod.
- What the process boundary *does* give us: (i) a Python plugin crashing / segfaulting doesn't bring down the .NET runtime; (ii) Python-side memory leaks don't accumulate in the .NET GC heap; (iii) per-plugin resource monitoring via standard OS tooling (pid-based cgroups, top, etc.).
- Supply-chain surface differs: Python plugins consume PyPI wheels, not NuGet packages. `uv.lock` hash pinning provides build-time integrity; runtime integrity depends on the OCI image signing the operator already uses.

### Decision (Q8): **Trusted-plugin posture, unchanged from v0.18. Process boundary is a reliability boundary, not a security boundary.**

Operational guidance to add to docs:
- Audit plugin `uv.lock` as part of the same supply-chain process that audits .NET plugin NuGet pins.
- The plugin's Python code can read any secret the runtime container can read; granular secret scoping is not v1.
- For multi-tenant isolation, run separate runtime pods per trust boundary.

Untrusted code execution is Track B (separate spike) and keeps its own security review.

---

## Q9 — Declarative manifest extension (Track C)

### Evidence

- Existing `AgentManifest.Tools` is a `IReadOnlyList<ToolRef>` where `ToolRef(Name, Source = null)`. Source is a free-form string; MCP tools use `"mcp:server-name"` as convention. No enum; kind is discriminated by prefix.
- `McpToolSource` is the generic mechanism; once a Python plugin's MCP server is registered as an MCP source at the silo level, its tools are reachable via the existing `ToolRef(Name, Source = "mcp:weather-python")` shape.
- No new manifest schema required. A declarative agent can reference a Python tool today with one line, once the plugin is loaded:
```yaml
tools:
  - name: get_weather
    source: mcp:weather-python
```

### Decision (Q9): **No manifest schema change. Python tools reach agents via the existing MCP source-prefix shape.**

The `plugin.yaml` + `pyproject.toml` descriptors ARE the plugin's manifest. The *agent's* manifest stays thin — it references tools by name + source. The tool-discovery path is: plugin loaded at startup → `McpToolSource` populated → tool appears in `IToolRegistry.Tools` → agent manifest's `ToolRef` resolves against the registry.

Optional syntactic sugar (v1.1+ polish): `source: python:weather-python` as an alias for `mcp:weather-python` with the semantics that the source MUST be a Python-plugin-backed MCP server. Low priority; the `mcp:` prefix works.

---

## Q10 — Phasing and dependencies

### Evidence

- Track A depends on: v0.7 MCP inbound/outbound (shipped), v0.18 .NET plugin model (shipped), v0.22 hot-reload infrastructure (shipped).
- Track A extends the v0.22 hot-reload story: `IPluginReloadHook` is .NET-plugin-specific (references `PluginDescriptor`); the Python equivalent is subprocess restart + MCP client re-handshake. Similar shape, separate code path.
- `Vais.Agents.Sandbox` (Track B) can be a standalone package with zero Orleans dependency — the sandbox is a tool that any runtime can invoke, not tied to grain lifecycle.
- SGR schema-only tool pattern (see §"Related observations" below) is a separate design concern — doesn't block this pillar.

### Decision (Q10): **One pillar for Track A + C (free consequence). Track B is a separate pillar later. SGR insight is tracked separately.**

Milestone proposal:
- **v0.23 Pillar — Python plugins.** Track A as MCP-server subprocesses + uv packaging + supervision service + hot-reload extension for Python. Track C is free.
- **v0.24 or later — Code execution sandbox.** Track B as a new package (`Vais.Agents.Sandbox` or similar), evaluated against E2B/Firecracker/gVisor productized alternatives.
- **Separate v0.XX — Schema-guided tool loop.** The SGR "empty-body schema-only tool" pattern (Reasoning/Planning as structured-output schemas, not effectful tools) is a tool-dispatch-shape change, not a polyglot-plugin change. File as its own spike.

---

## Related observations

### SGR "schema-only tool" pattern — capture for a future spike

The SGR Agent Core review surfaced one concept worth filing for later: tools that have no side effects, existing purely as Pydantic schemas that constrain the LLM's structured output. `ReasoningTool`, `GeneratePlanTool`, `ClarificationTool` in SGR all have empty `__call__` bodies — they are control-flow signals shaped as tool calls. SGR uses a discriminated-union structured-output call that emits `{reasoning, toolName, args}` atomically.

In `.NET` terms, this is a `JsonPolymorphic` / `JsonDerivedType` shape over `ITool` implementations, where "schema-only" tools have a no-op `InvokeAsync` that just threads a state signal back to the agent loop. Out of scope for the polyglot-plugins pillar; queue a separate spike (`actor-agents-oss-schema-guided-tools-spike.md`) if partners ask.

---

## Out of scope for this pass

- **Agent-level Python plugins** (a Python type implementing the equivalent of `IAiAgent`). Covered by A2A cross-runtime refs. Reopened only if partner demand surfaces concrete use-cases A2A can't meet.
- **Untrusted code execution sandbox** (Track B). Separate spike, different protocol, different trust model.
- **Node / Go / Rust plugins.** MCP has SDKs for all three; once the Python plugin's lifecycle story is solid, extending to Node is small (swap interpreter + descriptor). Explicitly deferred to follow-up pillars, one per language.
- **In-process Python embedding.** Disqualified by evidence (Q2). Revisit only if per-interpreter GIL (PEP 684) + wrapper support makes it viable, AND wasmtime-dotnet reaches WASI preview 2 parity with scientific-wheel support.
- **Streaming Python tool output** as token-by-token `CompletionUpdate` deltas. MCP progress notifications cover periodic updates; true token streaming is agent-level, not tool-level.
- **Python tool hot-reload in v1.** v0.22 hot-reload ships for .NET plugins. Python subprocess restart is a straightforward extension but adds test surface; defer to v0.23.x polish.

---

## Open items (resolved)

1. **Does the Orleans grain scheduler interact badly with CPython GIL?** Moot — in-process disqualified (Q2), subprocess boundary means GIL lives in the Python process, grain scheduler untouched.
2. **Production pythonnet-in-Orleans evidence?** None found. Combined with maintainer statements on multi-interpreter, this is a confirmation to not pursue that path.
3. **uv reliability at Orleans silo startup within 5s budget?** Confirmed: ~1.5s cold / ~200ms warm for a 20-package plugin; pre-baked OCI layer sidesteps even this (interpreter already installed).
4. **Pyodide 2026 ecosystem?** Irrelevant once Pyodide-via-Wasmtime is disqualified. LangChain-Sandbox pattern (Pyodide-in-Deno subprocess) noted for the future sandbox spike.
5. **Deno vs. V8 isolates for Track B?** Noted in the sandbox spike seed; irrelevant to this track.

---

## Landing verdict

The first pass delivers **Python plugins as MCP-server subprocesses**, packaged via `uv` with pre-baked venvs in OCI layers, supervised by a new `IPythonPluginHost` hosted service, and integrated into the existing `IToolRegistry` path without new abstractions. Scope is tool-level only; full-agent Python replacement is covered by A2A. Untrusted code sandbox and in-process Python are explicitly deferred with rationale.

**Scope for v0.23-preview is now frozen:**

1. New `IPythonPluginHost` hosted service — supervises Python subprocesses; health probes; restart-with-backoff.
2. `plugin.yaml` + `pyproject.toml` descriptor formats (Q7); `[tool.vais.plugin]` + `[project.entry-points."vais.plugins"]`.
3. MCP stdio as the one-and-only plugin protocol (Q4). Reuses `McpToolSource` + `McpBackedTool`.
4. uv-based packaging with `--relocatable` venv (Q3). Build-time bake; runtime `uv sync` as opt-in dev fallback.
5. New URNs: `urn:vais-agents:python-plugin-load-failed`, `urn:vais-agents:python-plugin-abi-mismatch`, `urn:vais-agents:python-plugin-exited`, `urn:vais-agents:python-plugin-unavailable`, `urn:vais-agents:python-plugin-handshake-timeout`.
6. Trusted security posture, unchanged from v0.18 .NET plugins (Q8).
7. Sample plugin: a minimal `weather-python` reference with `pyproject.toml`, `uv.lock`, `server.py` using the Python MCP SDK, and `Dockerfile.overlay`.
8. Docs updates: new guide `guides/package-a-python-plugin.md` mirroring the .NET one; concept `concepts/polyglot-plugins.md` explaining the Python-as-MCP-server model; deferred-backlog entries flipped to SHIPPED.

**Out of scope explicitly (restated):**

- Agent-level Python plugins (A2A covers).
- Untrusted code sandbox (Track B → separate spike).
- Node/Go/Rust plugins (one-per-language follow-up pillars).
- In-process Python (disqualified by evidence).
- Token-streaming tool output (not in MCP model; agent-level concern).
- SGR schema-only tool pattern (separate spike track).

**Ready for pillar plan.** Next doc: `plans/actor-agents-oss-v0.23-python-plugins-pillar.md` — locks the PR sequence with per-PR checklists, acceptance criteria, and timeline.

---

## Evidence sources

Compiled from the 6 parallel research streams on 2026-04-24.

### Python MCP SDK (verified post-synthesis)

- [mcp on PyPI](https://pypi.org/project/mcp/) — 1.27.0 (Apr 2, 2026), Anthropic-maintained
- [modelcontextprotocol/python-sdk](https://github.com/modelcontextprotocol/python-sdk)
- [CVE-2026-30623 advisory (liteLLM blog, Apr 15 2026)](https://docs.litellm.ai/blog/mcp-stdio-command-injection-april-2026) — stdio command injection
- [Apigene production MCP guide 2026](https://apigene.ai/blog/python-mcp-server) — stdio concurrency limits

### uv Windows relocatable (verified post-synthesis)

- [uv issue #15751](https://github.com/astral-sh/uv/issues/15751) — portable-mode tracker (open)
- [uv issue #13986](https://github.com/astral-sh/uv/issues/13986) — `.venv` partial-state bug
- [uv issue #17678](https://github.com/astral-sh/uv/issues/17678) — Windows venv re-creation quirk

### Python.NET / CSnakes

- [pythonnet releases](https://github.com/pythonnet/pythonnet/releases) — 3.0.5 stable, 3.1.0-rc0
- [pythonnet discussion #2397](https://github.com/pythonnet/pythonnet/discussions/2397) — maintainer confirms no multi-interpreter support
- [pythonnet issue #109](https://github.com/pythonnet/pythonnet/issues/109), [#964](https://github.com/pythonnet/pythonnet/issues/964), [#1587](https://github.com/pythonnet/pythonnet/issues/1587) — GIL + async hangs
- [pythonnet issue #499](https://github.com/pythonnet/pythonnet/issues/499) — Initialize/Shutdown memory leak
- [PEP 684 — per-interpreter GIL](https://peps.python.org/pep-0684/)
- [CSnakes](https://github.com/tonybaloney/CSnakes), [Talk Python #486](https://talkpython.fm/episodes/show/486/csnakes-embed-python-code-in-.net)

### Pyodide / WASM

- [Pyodide discussion #5145](https://github.com/pyodide/pyodide/discussions/5145), [issue #558](https://github.com/pyodide/pyodide/issues/558) — Pyodide not runnable under Wasmtime
- [PEP 816 — WASI Support](https://peps.python.org/pep-0816/), [VMware Labs WebAssembly Language Runtimes](https://github.com/vmware-labs/webassembly-language-runtimes/tree/main/python)
- [wasmtime-dotnet](https://github.com/bytecodealliance/wasmtime-dotnet) — v34.0.2, Aug 2025
- [Cloudflare Python Workers cold-start](https://blog.cloudflare.com/python-workers-advancements/)
- [Cyera Research: Cellbreak (CVE-2026-5752)](https://www.cyera.com/research/cellbreak-grists-pyodide-sandbox-escape-and-the-data-at-risk-blast-radius)
- [Platform.uno — State of WASM 2025-2026](https://platform.uno/blog/the-state-of-webassembly-2025-2026/), [Henrik Røn — .NET 10 dropped WASI](https://henrikrxn.github.io/blog/Wasi-dotnet-10/)

### Sandbox landscape (seed for Track B spike)

- [Deno security](https://docs.deno.com/runtime/fundamentals/security/), [Deno issue #26202](https://github.com/denoland/deno/issues/26202) — V8 external-memory bound gap
- [ClearScript V8 maxheap](https://microsoft.github.io/ClearScript/Reference/html/P_Microsoft_ClearScript_V8_V8Runtime_MaxHeapSize.htm), [ClearScript issue #558](https://github.com/microsoft/ClearScript/issues/558)
- [OpenAI Code Interpreter runtime — gVisor](https://itnext.io/openais-code-execution-runtime-replicating-sandboxing-infrastructure-a2574e22dc3c)
- [E2B Firecracker benchmarks](https://e2b.dev/blog/firecracker-vs-qemu), [Northflank sandbox comparison](https://northflank.com/blog/firecracker-vs-gvisor)
- [LangChain Sandbox](https://github.com/langchain-ai/langchain-sandbox) (archived Jan 2026), [Pydantic mcp-run-python](https://github.com/pydantic/mcp-run-python)

### uv

- [uv releases](https://github.com/astral-sh/uv/releases), [uv BENCHMARKS.md](https://github.com/astral-sh/uv/blob/main/BENCHMARKS.md)
- [uv locking + syncing](https://docs.astral.sh/uv/concepts/projects/sync/), [uv Python versions](https://docs.astral.sh/uv/concepts/python-versions/)
- [uv PR #5515 — `--relocatable`](https://github.com/astral-sh/uv/pull/5515), [uv issue #15751](https://github.com/astral-sh/uv/issues/15751)
- [PEP 621](https://peps.python.org/pep-0621/)

### SGR Agent Core

- [agents.yaml.example](https://raw.githubusercontent.com/vamplabAI/sgr-agent-core/main/agents.yaml.example), [config.yaml.example](https://raw.githubusercontent.com/vamplabAI/sgr-agent-core/main/config.yaml.example)
- [base_tool.py](https://raw.githubusercontent.com/vamplabAI/sgr-agent-core/main/sgr_agent_core/base_tool.py), [next_step_tool.py](https://raw.githubusercontent.com/vamplabAI/sgr-agent-core/main/sgr_agent_core/next_step_tool.py)
- [reasoning_tool.py](https://raw.githubusercontent.com/vamplabAI/sgr-agent-core/main/sgr_agent_core/tools/reasoning_tool.py), [sgr_tool_calling_agent.py](https://raw.githubusercontent.com/vamplabAI/sgr-agent-core/main/sgr_agent_core/agents/sgr_tool_calling_agent.py)
- [docs/en/framework/tools.md](https://raw.githubusercontent.com/vamplabAI/sgr-agent-core/main/docs/en/framework/tools.md)

### Internal codebase (source file paths)

- `src/Vais.Agents.Abstractions/ITool.cs:18` — `ITool` contract
- `src/Vais.Agents.Abstractions/IToolRegistry.cs:11` — registry shape
- `src/Vais.Agents.Abstractions/IToolSource.cs:22` — discovery contract
- `src/Vais.Agents.Abstractions/IToolCallDispatcher.cs:26` — dispatch
- `src/Vais.Agents.Core/AggregatingToolRegistry.cs:25` — multi-source aggregation
- `src/Vais.Agents.Core/DefaultToolCallDispatcher.cs:39` — default dispatcher with guardrails + journaling
- `src/Vais.Agents.Protocols.Mcp.Server/StdioAgentServerHost.cs:30` — stdio MCP server host
- `src/Vais.Agents.Protocols.Mcp/McpToolSource.cs:32` — MCP → `IToolSource` bridge
- `src/Vais.Agents.Protocols.Mcp/McpBackedTool.cs` — per-tool wrapper
- `src/Vais.Agents.Abstractions/AgentManifest.cs:40`, `:105` — manifest + `ToolRef` shape
- `src/Vais.Agents.Runtime.Plugins/IPluginHandlerRegistry.cs:13` — v0.18 registry
- `src/Vais.Agents.Runtime.Plugins/IPluginReloader.cs:46` — v0.22 reload hook
