# PluginAgentResearchPlanner

A research assistant agent whose planning tools are contributed by a Python MCP plugin managed by the Vais runtime. The .NET runtime handles the agent loop (LLM calls, history, streaming); Python handles planning logic (decomposition, scoring, summarization).

**Concepts:** [polyglot plugins](../../docs/concepts/polyglot-plugins.md), [package a Python plugin](../../docs/guides/package-a-python-plugin.md), [tools](../../docs/concepts/tools.md).
**Needs API key:** `TAVILY_API_KEY` for web search; `ANTHROPIC_API_KEY` for the LLM.
**Code:** Python + YAML only — no C# required.

---

## Layout

```
PluginAgentResearchPlanner/
├── research-planner/           # Python plugin
│   ├── plugin.yaml             # runtime descriptor
│   ├── pyproject.toml          # Python metadata + [tool.vais.plugin]
│   ├── Dockerfile.overlay      # layer into runtime image
│   └── src/research_planner/
│       ├── __init__.py
│       ├── server.py           # MCP entrypoint (FastMCP)
│       ├── planner.py          # heuristic planning logic
│       └── schemas.py          # Pydantic tool arg/return models
└── research-agent.yaml         # declarative agent manifest
```

---

## Quickstart

### 1. Build and install the plugin venv

```bash
cd samples/PluginAgentResearchPlanner/research-planner
uv sync --frozen        # requires uv — https://docs.astral.sh/uv/
uv venv --relocatable   # rewrite shebangs to relative paths for portability
```

### 2. Start the runtime with the plugin mounted

```bash
cd oss/agentic
docker build -f src/Vais.Agents.Runtime.Host/Dockerfile -t vais-agents-runtime:local .
docker run -d \
  -p 8080:8080 \
  -v "$(pwd)/samples/PluginAgentResearchPlanner/research-planner:/var/lib/vais/plugins/research-planner" \
  -e TAVILY_API_KEY="$TAVILY_API_KEY" \
  -e ANTHROPIC_API_KEY="$ANTHROPIC_API_KEY" \
  vais-agents-runtime:local
```

Verify the plugin loaded:

```bash
curl http://localhost:8080/healthz   # → ok
vais plugins list                   # → research-planner  Ready  pid=...
```

### 3. Deploy the agent

```bash
vais config set-context local --server http://localhost:8080
vais apply -f samples/PluginAgentResearchPlanner/research-agent.yaml
```

### 4. Invoke

```bash
vais invoke research-agent --text "What's driving the 2026 energy-storage cost decline?"
```

The runtime will call `decompose_task`, `score_plan_completeness`, `tavily_search` (once per sub-question), and `summarize_findings` — the first three are JSON-RPC calls into the Python subprocess; the last is a remote MCP call to Tavily.

---

## Shipping with Dockerfile.overlay

To bake the plugin into a production image:

```bash
cd samples/PluginAgentResearchPlanner
docker build -f research-planner/Dockerfile.overlay -t my-registry/research-planner-runtime:0.23.0 .
```

The overlay copies the fully-built plugin directory (including `.venv/`) into the base runtime image at `/var/lib/vais/plugins/research-planner`.

> **Note on `uv.lock`:** A hash-pinned lockfile requires `uv` to generate and is therefore not committed to this repository. Run `uv lock` in the `research-planner/` directory to produce it before building for production.

---

## How it works

The manifest declares `transport: plugin` for `research-planner`. This tells the runtime that the MCP server's subprocess is managed via `IPythonPluginHost` — no `command` or `url` is required. At silo startup:

1. `IPythonPluginHost` scans `/var/lib/vais/plugins/` for `plugin.yaml` files with `runtime: python`.
2. It spawns `.venv/bin/python src/research_planner/server.py` in the plugin directory.
3. After the MCP handshake, `tools/list` is called and the three tools are registered as an `IToolSource` under the name `research-planner`.
4. The agent's tool registry resolves `source: mcp:research-planner` to that source — the same lookup path as any external MCP server.

From the LLM's perspective, `decompose_task`, `score_plan_completeness`, and `summarize_findings` are indistinguishable from .NET-native tools.
