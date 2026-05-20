# Polyglot plugins

The runtime can load tool-contributing plugins authored in languages other than C#. The supported runtime today is Python. A Python plugin is an MCP server spawned as a subprocess; the runtime manages its lifecycle, negotiates the handshake, and exposes its tools to the agent's tool registry exactly as if they were .NET-native tools.

## Concept

The .NET plugin model (v0.18) routes an entire `IAiAgent` implementation to a DLL. Python plugins work at a narrower scope: they contribute **tools**, not an agent loop. The LLM orchestration loop, history, streaming, and guardrails remain in the .NET runtime. Python owns the planning or domain logic that is easier or faster to write in Python.

```
┌─────────────────── .NET runtime ───────────────────────────────────────┐
│                                                                         │
│  AiAgentGrain  ──→  IToolCallDispatcher  ──→  McpBackedTool            │
│                                                      │                  │
│           ┌──────────────────────────────────────────┘                  │
│           ↓  JSON-RPC over stdio                                        │
│  ┌──── Python subprocess ─────────────────────────────────────────────┐ │
│  │  FastMCP server                                                     │ │
│  │  ├── decompose_task(args) → list[str]                               │ │
│  │  ├── score_plan_completeness(question, subquestions) → ScoredPlan   │ │
│  │  └── summarize_findings(args) → str                                 │ │
│  └─────────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
```

## Plugin directory structure

Each plugin lives in its own subdirectory under the configured plugins root (default `/var/lib/vais/plugins`):

```
research-planner/
├── plugin.yaml                 # runtime descriptor
├── pyproject.toml              # Python metadata + [tool.vais.plugin]
├── Dockerfile.overlay          # layer into runtime image
└── src/research_planner/
    ├── __init__.py
    ├── server.py               # MCP server entrypoint (FastMCP)
    ├── planner.py              # domain logic
    └── schemas.py              # Pydantic tool contracts
```

The `.venv/` directory is produced by `uv sync --frozen && uv venv --relocatable` and is **not** committed to source control; it is either baked into the container image or mounted at runtime.

## plugin.yaml — the runtime descriptor

```yaml
apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: research-planner
spec:
  runtime: python
  entrypoint: src/research_planner/server.py
  python:
    version: "3.13"
    interpreter: .venv/bin/python   # resolved relative to the plugin directory
  health:
    handshakeTimeoutSeconds: 5
    restartPolicy: exponentialBackoff
```

`restartPolicy` accepts `exponentialBackoff` (default) or `never`. With `exponentialBackoff` the runtime re-spawns the subprocess after a crash and retries tool calls after the next handshake succeeds.

## pyproject.toml — the Python-side contract

```toml
[tool.vais.plugin]
targetApiVersion = "0.24"
tools = ["decompose_task", "score_plan_completeness", "summarize_findings"]
```

`targetApiVersion` must match the runtime's ABI version. `tools` is a cross-check: after the MCP handshake, the runtime calls `tools/list` and warns if a declared tool is absent from the server's response.

## Startup lifecycle

At silo startup, `IPythonPluginHost` (registered by `AddPythonPlugins(...)`) scans the plugins directory:

1. For each `plugin.yaml` with `runtime: python`, parse descriptor + `pyproject.toml`.
2. Resolve the interpreter path relative to the plugin directory.
3. `Process.Start` with `stdin`/`stdout` redirected — the MCP protocol runs over stdio.
4. Send `initialize`; wait up to `handshakeTimeoutSeconds`.
5. Call `tools/list`; verify against `[tool.vais.plugin].tools`.
6. Register the live `McpClient` as an `IToolSource` under the plugin's name.

After step 6 the plugin's tools are visible to the tool registry. Agents that declare `source: mcp:<plugin-name>` in their tool list are wired up on their next `TranslateAsync` call.

## Declarative agent manifest

Reference the plugin using `transport: plugin` in `mcpServers`. No `command` or `url` is required — the subprocess is managed by the runtime.

```yaml
mcpServers:
  - name: research-planner
    transport: plugin
```

Then declare the individual tools:

```yaml
tools:
  - name: decompose_task
    source: mcp:research-planner
  - name: score_plan_completeness
    source: mcp:research-planner
  - name: summarize_findings
    source: mcp:research-planner
```

## Hot-reload (v0.25)

`PythonPluginWatcherService` watches each plugin directory for changes to `plugin.yaml`, `*.py`, and `pyproject.toml` using `FileSystemWatcher` with a 200 ms debounce. On a detected change:

1. `DefaultPythonPluginReloader.DrainAndRestartAsync` drains in-flight calls.
2. The old subprocess is terminated.
3. A new subprocess is spawned and the MCP handshake + `tools/list` flow repeats.
4. `TranslatorInvalidationHook` clears the manifest-translator cache for any agents using this plugin's tools.

Plugins implement `IPluginReloadHook` to receive a notification before and after each reload cycle (useful for metrics or audit). The runtime host wires `PythonPluginWatcherService` automatically when `AddPythonPlugins(...)` is called with a directory.

If a reload fails (subprocess crash, handshake timeout, unresolvable secret), the plugin enters `Error` state. The next file-system change triggers another reload attempt automatically.

## Secrets (v0.31)

Declare secrets in `plugin.yaml` under `spec.secrets` as a list of ref names. `ISecretResolver` resolves each name at startup and on hot-reload; unresolvable refs abort the load/reload with URN `urn:vais-agents:python-plugin-secret-resolution-failed`. Resolved values are injected as `VAIS_SECRET_<REF>=<value>` in the subprocess environment — the Python side reads them from `os.environ`.

## Error URNs

| URN | Meaning |
|---|---|
| `urn:vais-agents:python-plugin-load-failed` | Descriptor parse or file I/O error during load |
| `urn:vais-agents:python-plugin-abi-mismatch` | `targetApiVersion` does not match the runtime ABI |
| `urn:vais-agents:python-plugin-handshake-timeout` | MCP initialize did not complete within the budget |
| `urn:vais-agents:python-plugin-exited` | Subprocess exited unexpectedly |
| `urn:vais-agents:python-plugin-unavailable` | All restart attempts exhausted; tool calls fail |
| `urn:vais-agents:python-plugin-ambiguous-folder` | Multiple `plugin.yaml` files found in a single folder |
| `urn:vais-agents:python-plugin-secret-resolution-failed` | A `spec.secrets` ref could not be resolved; plugin skipped / hot-reload aborted |

## Shipping in production

Build and push an overlay image that bakes the plugin (including its `.venv/`) into the base runtime image:

```dockerfile
FROM ghcr.io/vais-agents/runtime:0.23.0-preview
COPY research-planner /var/lib/vais/plugins/research-planner
```

The overlay pattern is the same as v0.18 .NET plugins — only the plugin contents differ.

## See also

- [Package a Python plugin](../guides/package-a-python-plugin.md) — step-by-step guide
- [Runtime plugins](runtime-plugins.md) — the v0.18 .NET plugin baseline
- [Container plugins](container-plugins.md) — the OCI-image sibling plugin model (Docker / Kubernetes supervision, HTTP gateway protocol)
- [Polyglot agents](polyglot-agents.md) — the full-agent Python shim (vs the tool-scope plugins this doc covers)
- [Tools](tools.md) — how tools flow through the registry
- [PluginAgentResearchPlanner sample](../../samples/PluginAgentResearchPlanner/README.md)
