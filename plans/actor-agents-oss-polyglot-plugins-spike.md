# Polyglot plugins — spike

Research scope for bringing non-.NET code into the Vais.Agents runtime. Three distinct tracks, two of which (A and B) may become separate pillars. Track C (tool-level Python) may be absorbed into a future declarative-agent enhancement pillar.

Created 2026-04-24. **Status**: open.

---

## Background and motivation

v0.18 Pillar C shipped a .NET-only plugin model with an explicit deferral note: "Partners with Python / Node agents use A2A cross-runtime refs (Pillar E / v0.20) — their agent runs in its own runtime." That deferral stood because the A2A path covered the *coarse-grained* polyglot case — a separate agent process or service accessible over the network.

Two gaps remain:

1. **Tight coupling gap.** A2A agents are first-class *agents*, not plugins. They have their own lifecycle, their own memory, their own DI context. A Python tool or planner that should participate in an agent's *internal loop* — before the model sees the response, between retries, as a planning step — has no lightweight path. Spinning up a separate agent for this is architectural overkill and adds a round-trip that breaks the streaming pipeline.

2. **Code execution gap.** LLM-generated code (Python scripts, JS snippets) needs a sandboxed execution environment that is *isolated* (untrusted), *ephemeral* (per-call), and *resource-bounded*. This is structurally opposite to plugins, which are trusted, persistent, and packaged. The v0.18 backlog has no entry for sandboxed code execution; the streaming lifecycle gap note (`§10` in deferred-backlog) is adjacent but not equivalent.

A third observation from SGR Agent Core: the declarative YAML agent syntax there includes a `tools:` block that can reference Python-backed tools inline — not as separate agents, but as named callable steps within a single agent's plan. This suggests a third shape: **tool-level Python integration** as a declarative extension of the manifest schema, distinct from agent-level plugin replacement.

The spike must evaluate all three tracks independently and determine: (a) whether each has a viable implementation path within the Orleans + ASP.NET Core runtime model, (b) what each adds beyond A2A, and (c) whether they deserve separate pillars or share infrastructure.

---

## Track definitions

| Track | Description | Security model | Lifecycle | Analogy |
|---|---|---|---|---|
| **A — Plugin extension** | Trusted, packaged Python code that implements part of an agent's internal loop. Replaces or supplements .NET code in the plugin boundary. | Trusted (same as .NET plugin) | Per-silo, persistent | v0.18 `IAgentHandlerFactory`, but Python-backed |
| **B — Code sandbox** | Untrusted, ephemeral Python/JS execution for LLM-generated code. Isolated from host DI, resource-bounded, single-call lifetime. | Untrusted (sandboxed) | Per-call, ephemeral | Lambda / Deno Deploy execution model |
| **C — Declarative tool** | Python callable referenced in a YAML `tools:` block. Registered alongside other tool definitions; called by the agent's tool-invocation path. | Trusted (plugin-like) | Per-invocation (stateless) | MCP tool, but Python-local not remote |

Tracks A and B should not share infrastructure. Track C may reuse Track A's interop mechanism.

---

## Q1 — What capability does Track A add beyond A2A?

### Evidence required

- Document the A2A invocation path (latency profile, streaming propagation, state sharing).
- Identify scenarios where A2A's round-trip semantics make it unsuitable: e.g., planning loops that run N times before the model call, guardrail implementations, tool-invocation hooks that must run in the same streaming window.
- List known partner requests or use-cases (from issues, Slack, or the SGR repo) that require tight coupling.

### Options to evaluate

- **A — nothing extra**: A2A already covers all meaningful polyglot cases; Track A adds complexity without value. Close the question with a documented rationale.
- **B — in-process interop**: Python runs inside the runtime process (via embedded interpreter or IPC to a co-located sidecar). Suitable for low-latency inner-loop tools.
- **C — per-pod sidecar**: Python process runs alongside the runtime container (separate PID, same pod). Higher isolation than in-process; higher latency than in-process. Reuses protocol from gRPC/stdio MCP.

Discrimination criteria: latency budget for the target use-case, support for streaming (token-by-token propagation), state sharing needs (does the Python code need access to agent history?).

---

## Q2 — In-process Python/.NET interop options

### Evidence required

- Evaluate **Python.NET (pythonnet)** (CPython embedding via FFI):
  - Does it support async C# ↔ Python dispatch without deadlocking on the GIL?
  - Can one `.NET` process host multiple Python environments (venv isolation per plugin)?
  - What is the native-dep story (numpy, torch)? Does `libpython` bring in native extensions?
  - Orleans grain async model: grains use `Task`-returning methods scheduled on a cooperative thread pool. Does the GIL interact with the Orleans scheduler?
- Evaluate **WASM (Wasmtime + `wasm-dotnet`)** for Python via Pyodide:
  - Does Pyodide's CPython→WASM port support the Python packages likely to be used (pure-Python only, or native too)?
  - What is startup cost per invocation vs. warm-pool cost?
  - Can WASM sandbox interact with .NET objects (or only JSON-serialized values)?
- Evaluate **subprocess + stdio (MCP-style)**:
  - Already proven in MCP inbound (v0.7); Python plugin runs as a subprocess, communicates JSON-RPC over stdio.
  - What is the process lifecycle model? One process per plugin (persistent) or one per call (ephemeral)?
  - Can stdout streaming be propagated back through the agent's `IAsyncEnumerable<CompletionUpdate>`?

### Options to evaluate

- pythonnet in-process (low-latency, GIL risk)
- Pyodide/WASM sandbox (medium-latency, strong isolation, limited ecosystem)
- stdio subprocess (high-isolation, medium-latency, MCP-compatible)
- gRPC sidecar (separate process/pod, highest isolation, highest latency)

Discrimination criteria: GIL compatibility with Orleans async, venv isolation, native extension support, streaming propagation.

---

## Q3 — Python environment isolation and packaging

### Evidence required

- How does the existing v0.18 .NET plugin packaging model (per-plugin subfolder, `AssemblyDependencyResolver`) translate to Python?
- Evaluate **uv** (fast Python env tool) for per-plugin venv creation at runtime startup.
- Evaluate the "Python plugin as OCI layer" shape: a plugin subfolder contains a `python/` directory with a frozen `requirements.txt` + `venv/`; startup bootstraps the interpreter pointing at that venv.
- Determine whether Python native extensions (C extensions, .so/.pyd files) must be supported in v1 of this feature, or can be deferred.
- Document the ABI story: what replaces `[assembly: VaisPlugin(targetApiVersion: "0.18", ...)]` for Python? Candidates: `plugin.toml`, a `pyproject.toml` metadata block, a `plugin.yaml` manifest.

### Options to evaluate

- **A — venv per plugin**, bootstrapped at startup by `uv sync` or pre-baked into the plugin directory.
- **B — system Python + pip install on demand** (dev-only; not suitable for production).
- **C — Pyodide bundle** (WASM path): Python environment is the WASM image; plugins ship `.whl` files that Pyodide installs into the WASM FS.
- **D — Docker-in-Docker or ephemeral container** (code sandbox, Track B): each code execution creates a container with the requested Python version + deps, runs the script, returns output, destroys the container.

---

## Q4 — Protocol for cross-boundary calls (Track A sidecar / Track C tools)

### Evidence required

- Map the existing MCP inbound protocol (v0.7) — JSON-RPC 2.0 over HTTP SSE or stdio — to the plugin call model. Can the same protocol serve plugin invocations?
- Evaluate **gRPC** for bidirectional streaming support: plugin → runtime streaming responses, runtime → plugin passing agent context.
- Evaluate **named pipes** for in-pod IPC (lower overhead than loopback TCP for co-located processes).
- Determine whether the protocol must support **streaming** (for Track A plugins that yield tokens) or whether request/response (for Track C tools) is sufficient.

### Open question: plugin boundary — agent-level or tool-level?

v0.18 plugins replace an entire `IAiAgent` implementation. The SGR Tools concept suggests a finer granularity: a Python function is registered as a *tool*, invoked by the agent's tool-invocation loop, not as a full agent replacement. Determine:

- Is "Python tool in a declarative agent" a Track C extension of the declarative manifest (`tools:` block with `kind: Python`)?
- Or is it a specialisation of Track A (a Python plugin that implements `IAgentHandlerFactory` and returns a `StatefulAiAgent` with Python-backed tools in its registry)?

The answer shapes whether `Vais.Agents.Abstractions` needs a new `IPythonToolDescriptor` type or whether the existing `ToolDefinition` / `IToolRegistry` surface is sufficient.

---

## Q5 — Orleans integration shape for Track A

### Evidence required

- Grains are per-agent instances scheduled by the Orleans runtime. A plugin that is in-process (pythonnet) interacts with the grain's thread context. A plugin that is a sidecar process does not share the grain's async context.
- Document the proxy model: does the Orleans grain call Python via a DI-injectable `IPythonPluginProxy`? Does the proxy live at grain scope (one Python process per grain) or silo scope (one Python process per plugin, shared across all grains on the silo)?
- Evaluate the **grain-scoped proxy** model: each grain activation creates (or acquires from a pool) a Python interpreter instance with the plugin's state. Matches Orleans' per-grain activation lifecycle but may be expensive.
- Evaluate the **silo-scoped shared proxy** model: one Python process per plugin per silo, all grains on that silo share it. Cheaper, but requires thread-safety in the Python code (GIL or explicit locking).
- Evaluate the **stateless call** model: the Python code is purely functional (no per-grain state); each call is a JSON-in / JSON-out invocation. Cheapest, but eliminates the ability for Python tools to maintain per-agent conversation context.

---

## Q6 — Track B: sandboxed code execution architecture

### Evidence required

- Determine whether **Python** or **JavaScript/TypeScript (Deno / Node with --sandbox)** is the better first target for code sandbox. Consider:
  - LLM-generated code is more commonly tested in Python for data-science tasks; JavaScript may be more natural for UI/web tasks.
  - Deno has a mature permission system (no network, no disk, no env by default) that maps well to sandbox semantics.
  - CPython has no built-in sandbox; alternatives are `seccomp`, restricted subprocess, or WASM.
- Evaluate execution models for the sandbox:
  - **Ephemeral subprocess** (os.fork + seccomp on Linux, Job Objects on Windows): per-call isolation, highest security, overhead per call.
  - **Deno `--allow-none`**: Deno subprocess with all permissions disabled; script runs in V8 with no I/O. Works on any OS.
  - **Pyodide in WASM**: Python in WASM has no native I/O by default; scripts that try to open files or sockets fail silently or with WASM traps.
  - **Docker / OCI container ephemeral**: heaviest isolation, supports native deps; impractical for per-call invocation; better for longer-running jobs.
- Determine resource bounds: CPU time limit, memory cap, output size cap. Who enforces them? (OS kill, CancellationToken timeout, or WASM fuel limit.)
- Determine the surface: is code sandbox exposed as a tool (Track C shape), as a dedicated `ICodeExecutor` service, or as an agent capability flag (`ExecuteCode: true` in manifest)?

---

## Q7 — ABI and versioning for non-.NET plugins

### Evidence required

- The v0.18 ABI contract (`VaisPluginAttribute(targetApiVersion: "0.18")`) is .NET-only. Non-.NET plugins need a language-agnostic ABI descriptor.
- Evaluate the **`plugin.yaml` manifest** approach: each plugin ships a `plugin.yaml` in its root that declares `apiVersion`, `runtime` (dotnet|python|wasm), `entrypoint`, and `handlers`. The runtime loader reads this before deciding how to load the plugin.
- Evaluate whether the existing `VaisRuntimeAbi.CurrentVersion` + major.minor match logic can be reused (the check just compares a string; the source is the plugin's manifest, not a .NET attribute).
- Determine what "shared types" means for Python: .NET's `PluginAssemblyLoadContext.SharedAssemblies` ensures type identity at the ABI boundary; Python uses JSON/protobuf serialization, so there is no CLR type identity to protect. But the *schema* of messages (e.g., `AgentManifest`, `CompletionRequest`, `ChatTurn`) must be versioned. Evaluate protobuf vs. JSON Schema vs. a code-generated Python SDK.

---

## Q8 — Security posture for Track A vs. Track B

### Evidence required

- Track A (trusted plugin): same security posture as v0.18 .NET plugins — full `IServiceProvider` access, runs inside the runtime container. Document what "trusted Python plugin" means operationally: code review requirements, supply-chain (Python package audit vs. NuGet audit), pinned `requirements.txt` hash vs. floating deps.
- Track B (untrusted sandbox): define the threat model. Adversary = LLM hallucination producing malicious code. What can sandbox-escaped code access? (Host network, env vars, file system, other grains.) For each access type, what is the mitigation?
- Evaluate OS-level isolation for Track B:
  - Linux: `seccomp` profile (deny syscalls beyond `read`/`write`/`exit`/`mmap`), combined with `cgroups` for memory/CPU.
  - Windows: Job Objects for CPU/memory; `CreateProcessWithTokenW` with restricted token for file access.
  - Cross-platform: WASM sandbox avoids OS-specific primitives but limits Python ecosystem.

---

## Q9 — Declarative manifest extension (Track C)

### Evidence required

- Review the current `AgentManifest` YAML schema (v0.6 Pillar) for the `tools:` block structure.
- Determine whether `tools:` already supports an extensible `kind:` discriminator. If so, adding `kind: Python` requires only a new tool descriptor type + a Python dispatcher in the tool-invocation path.
- Evaluate the SGR Agent Core `tools:` YAML pattern: tools there are named, have credential injection points, and are selected by the schema-guided reasoning planner. Map this to the Vais.Agents `IToolRegistry` / `ToolDefinition` abstraction.
- Determine where the Python tool code lives for Track C: inline in the manifest (a `script:` field), referenced as a file path (loaded at startup), or referenced by a plugin name (plugin must be loaded as Track A first).

---

## Q10 — Phasing and dependencies

### Evidence required

- List the pillars that Track A/B/C depend on:
  - Track A depends on v0.18 plugin model (shipped) and possibly v0.22 hot-reload (shipped).
  - Track B has no hard dependencies but benefits from the streaming pipeline (v0.10/v0.12) for streaming sandbox output.
  - Track C depends on the declarative manifest schema (v0.6) and IToolRegistry (v0.4 Tools pillar).
- Determine whether Track A must precede Track C (if Track C reuses Track A's interop) or whether they can be developed independently.
- Determine whether Track B (sandbox) could be implemented as a standalone library with zero dependencies on the Orleans runtime — i.e., can `Vais.Agents.Sandbox` be a separate package usable by any .NET application?
- Propose a milestone assignment for each track (e.g., v0.23 for Track A spike, v0.24/v0.25 for Track B depending on security choices).

---

## Candidates to evaluate (summary table)

| Axis | Candidate A | Candidate B | Candidate C |
|---|---|---|---|
| Track A interop | Python.NET (in-process) | stdio subprocess (MCP-style) | gRPC sidecar |
| Track B sandbox | Pyodide/WASM | Deno `--allow-none` | seccomp subprocess |
| Track C tool shape | `kind: Python` in manifest | Python plugin implementing `IToolProvider` | Inline `script:` in manifest |
| Python env isolation | uv venv per plugin folder | Pre-baked venv in OCI layer | Pyodide wheel bundle |
| Cross-boundary serialization | JSON (existing `CompletionRequest` shape) | Protobuf (generated SDK) | MessagePack |
| ABI descriptor | `plugin.yaml` (language-agnostic) | `pyproject.toml` metadata block | Extend `VaisPluginAttribute` pattern to a `.json` sidecar |

---

## Explicit out-of-scope for this spike

- **Node.js plugins** (addressed after Python; Deno may appear as a Track-B sandbox candidate).
- **Rust / WASM plugins** (different audience; future spike).
- **Full Python SDK** (code-generated client SDK for building Python agents; depends on findings here for the serialization protocol).
- **Plugin signing / SBOM** (already deferred in v0.18; not reopened here).
- **Hot-reload for Python plugins** (v0.22 shipped hot-reload for .NET; Python equivalent is a Phase 4 open item).
- **MCP tool server as plugin** (MCP inbound v0.7 already covers this; Python MCP servers connect via existing inbound path, not plugin mechanism).

---

## Open items to resolve in findings

1. Does the Orleans grain scheduler (cooperative, work-stealing, via `TaskScheduler` override) interact with the CPython GIL in a way that risks deadlock? Needs a focused async-path test.
2. Is there a production-grade example of pythonnet embedded in an Orleans silo? Survey GitHub + Nuget; if none, the lack of evidence is itself a data point.
3. Can `uv` reliably create a venv during Orleans silo startup without blocking the `IsReady` health probe beyond the existing 5-second plugin-scan budget?
4. What does Pyodide's Python version + package compatibility look like in 2026? (Pyodide 0.26+ targets Python 3.12; check if scientific stack packages are available as wheels.)
5. Is the user's mention of JS sandboxes referring to Deno specifically, or to V8 isolates (via a library like `V8.Net` or `Jint`)? Clarify before Track B design.

---

## Related

- [v0.18 plugin model findings](actor-agents-oss-v0.18-plugin-model-findings.md) — .NET plugin baseline this spike extends.
- [v0.18 plugin model pillar](actor-agents-oss-v0.18-plugin-model-pillar.md) — PR sequence for the .NET plugin implementation.
- [v0.22 plugin hot-reload pillar](actor-agents-oss-v0.22-plugin-hot-reload-pillar.md) — hot-reload infrastructure that a Python plugin may eventually reuse.
- [v0.20 cross-runtime refs findings](actor-agents-oss-v0.20-cross-runtime-refs-findings.md) — A2A as the baseline for polyglot agents; defines what Track A must add beyond A2A to justify existence.
- [deferred backlog §3](../docs/roadmap/deferred-backlog.md#3-plugins--hosting) — non-.NET plugin deferral note + sandbox gap.
- [SGR Agent Core](https://github.com/vamplabAI/sgr-agent-core) — external Python agent framework with YAML tool declarations; inspiration for Track C.
