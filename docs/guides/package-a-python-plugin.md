# Package a Python plugin

**v0.23.** This guide walks through authoring a Python MCP plugin that the Vais runtime manages as a subprocess, making its tools available to any declarative agent.

**What you'll build:** a Python plugin with three tools (`decompose_task`, `score_plan_completeness`, `summarize_findings`), a declarative agent that uses them, and an overlay Dockerfile that ships both together.

**Prerequisites:**
- [uv](https://docs.astral.sh/uv/) ≥ 0.4 installed
- Python 3.13
- A running Vais runtime (see [install-the-runtime-locally](install-the-runtime-locally.md))
- `vais` CLI on your PATH

---

## 1. Create the plugin directory

```bash
mkdir -p my-planner/src/my_planner
cd my-planner
```

## 2. Write pyproject.toml

```toml
[project]
name = "my-planner"
version = "0.1.0"
requires-python = ">=3.13"
dependencies = [
  "mcp >=1.27,<2",
  "pydantic >=2.8,<3",
]

[tool.vais.plugin]
targetApiVersion = "0.23"
tools = ["decompose_task", "score_plan_completeness", "summarize_findings"]

[project.entry-points."vais.plugins"]
server = "my_planner.server:main"
```

Two fields matter for the runtime:
- `[tool.vais.plugin].targetApiVersion` — must match the runtime's ABI (`"0.23"` for v0.23).
- `[tool.vais.plugin].tools` — the tool names the runtime will cross-check against `tools/list` after the MCP handshake.

## 3. Write the MCP server

`src/my_planner/server.py`:

```python
from mcp.server.fastmcp import FastMCP
from pydantic import BaseModel, Field

mcp = FastMCP("my-planner")

class DecomposeArgs(BaseModel):
    question: str
    max_subquestions: int = Field(default=5, ge=1, le=10)

@mcp.tool()
def decompose_task(args: DecomposeArgs) -> list[str]:
    """Break a research question into answerable sub-questions."""
    topic = args.question.rstrip("?")
    return [f"Sub-question {i + 1} about: {topic}" for i in range(args.max_subquestions)]

@mcp.tool()
def score_plan_completeness(question: str, subquestions: list[str]) -> dict:
    """Score coverage of the plan."""
    return {"coverage_score": min(1.0, len(subquestions) / 5), "missing_angles": [], "rationale": "ok"}

@mcp.tool()
def summarize_findings(question: str, findings: list[str], max_length_chars: int = 2000) -> str:
    """Summarize a list of findings into a report."""
    body = "\n".join(f"{i + 1}. {f}" for i, f in enumerate(findings))
    return f"Summary: {question}\n\n{body}"[:max_length_chars]

def main() -> None:
    mcp.run(transport="stdio")
```

## 4. Write plugin.yaml

```yaml
apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: my-planner
spec:
  runtime: python
  entrypoint: src/my_planner/server.py
  python:
    version: "3.13"
    interpreter: .venv/bin/python
  health:
    handshakeTimeoutSeconds: 5
    restartPolicy: exponentialBackoff
```

`interpreter` is resolved relative to the plugin directory at runtime. Use `.venv/bin/python` (Linux/macOS) or `.venv/Scripts/python.exe` (Windows, if running outside Docker).

## 5. Build the venv

```bash
uv sync                  # create .venv from pyproject.toml + uv.lock
uv venv --relocatable    # rewrite shebangs to relative paths
```

`--relocatable` is required so the interpreter paths survive being copied into a container.

> **Tip:** Commit `uv.lock` (hash-pinned) to source control for reproducible builds. Do not commit `.venv/` — it is build output.

## 6. Test the server locally

```bash
.venv/bin/python src/my_planner/server.py
```

The server should start without output (it waits for JSON-RPC on stdin). Press `Ctrl-C` to stop.

## 7. Write the agent manifest

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: planner-agent
  version: "1.0"
spec:
  model:
    provider: anthropic
    id: claude-haiku-4-5
  systemPromptSpec:
    text: "You are a research planner. Use the available tools to decompose, validate, and summarize."
  tools:
    - name: decompose_task
      source: mcp:my-planner
    - name: score_plan_completeness
      source: mcp:my-planner
    - name: summarize_findings
      source: mcp:my-planner
  mcpServers:
    - name: my-planner
      transport: plugin
  protocols:
    - kind: Http
```

`transport: plugin` tells the runtime that this MCP server's subprocess is managed by `IPythonPluginHost` — no `command` or `url` is needed.

## 8. Mount the plugin and apply the manifest

```bash
# Start (or restart) the runtime with the plugin directory mounted
docker run -d \
  -p 8080:8080 \
  -v "$(pwd):/var/lib/vais/plugins/my-planner" \
  -e ANTHROPIC_API_KEY="$ANTHROPIC_API_KEY" \
  vais-agents-runtime:local

# Confirm the plugin is ready (REST endpoint — no dedicated CLI command)
curl http://localhost:8080/v1/plugins   # → [{"name":"my-planner","status":"Ready"}]

# Deploy the agent
vais apply -f planner-agent.yaml

# Invoke
vais invoke planner-agent --text "What drives battery cost reductions?"
```

## 9. Ship with an overlay Dockerfile

```dockerfile
FROM ghcr.io/vais-agents/runtime:0.23.0-preview
COPY . /var/lib/vais/plugins/my-planner
```

Build and push:

```bash
# From the my-planner/ directory
docker build -f Dockerfile.overlay -t my-registry/my-planner-runtime:latest ..
docker push my-registry/my-planner-runtime:latest
```

The overlay image includes the plugin (with `.venv/`) baked in. No volume mount required in production.

---

## Hot-reload (v0.25)

The runtime watches the plugin directory for changes and restarts the subprocess automatically — no silo restart required.

**What triggers a reload:** any change to `plugin.yaml`, `*.py` files, or `pyproject.toml` in the plugin directory. Changes are debounced for 200 ms so a sequence of saves fires one reload.

**Reload sequence:**
1. In-flight tool calls are drained (the subprocess handles the current batch).
2. The subprocess is terminated.
3. A fresh subprocess is spawned with the same MCP handshake + `tools/list` flow as startup.
4. The manifest-translator cache is invalidated so the next `TranslateAsync` picks up any changed tool declarations.

If a reload fails (e.g. a broken import), the plugin stays in the `Error` state until the next file change triggers another attempt.

**Development tip:** `touch plugin.yaml` forces a reload without changing any code — useful when iterating on environment or config.

**Reload abort conditions:** if any `spec.secrets` ref cannot be resolved at reload time, the reload is aborted and logged with `urn:vais-agents:python-plugin-secret-resolution-failed`. Fix the secret first, then re-save a source file to retry.

---

## Secrets (v0.31)

Declare secrets that the runtime should inject into the subprocess environment:

```yaml
# plugin.yaml
apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: my-planner
spec:
  runtime: python
  entrypoint: src/my_planner/server.py
  python:
    version: "3.13"
    interpreter: .venv/bin/python
  secrets:
    - MY_API_KEY
    - INTERNAL_DB_PASSWORD
  health:
    handshakeTimeoutSeconds: 5
    restartPolicy: exponentialBackoff
```

Each name in `spec.secrets` must match the pattern `[A-Za-z_][A-Za-z0-9_]*`. At startup (and on hot-reload) `ISecretResolver` resolves each ref — typically from Kubernetes `Secret` objects or environment variables on the silo. The resolved value is injected as `VAIS_SECRET_<REF>=<value>` in the subprocess environment.

Inside the Python plugin, read the secret with:

```python
import os
api_key = os.environ["VAIS_SECRET_MY_API_KEY"]
```

If any declared secret cannot be resolved, the plugin is skipped at startup (or the hot-reload is aborted). The failure is logged with URN `urn:vais-agents:python-plugin-secret-resolution-failed`.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Plugin not listed by `vais plugins list` | Missing or malformed `plugin.yaml` | Check the runtime logs: `urn:vais-agents:python-plugin-load-failed` |
| `abi-mismatch` in logs | `targetApiVersion` doesn't match runtime | Set `targetApiVersion = "0.23"` in `[tool.vais.plugin]` |
| `handshake-timeout` in logs | Server crashes on startup | Run `.venv/bin/python server.py` manually; look for import errors |
| Tool calls return errors | Tool not in `tools/list` response | Ensure `@mcp.tool()` name matches `[tool.vais.plugin].tools` |
| Plugin skipped at startup | `spec.secrets` ref unresolvable | Check `urn:vais-agents:python-plugin-secret-resolution-failed` in logs; ensure the secret exists in the silo's `ISecretResolver` source |
| Hot-reload not triggering | File watcher not watching that path | Only changes inside the plugin directory are watched; changes to files outside (e.g. shared libraries) require a silo restart |

## See also

- [Polyglot plugins concept](../concepts/polyglot-plugins.md)
- [Problem details URNs reference](../reference/problem-details-urns.md)
- [PluginAgentResearchPlanner sample](../../samples/PluginAgentResearchPlanner/README.md)
