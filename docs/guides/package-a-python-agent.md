# Package a Python agent

This guide walks through building a first-class Python agent plugin — one that is deployed, activated, and durably managed by the VAIS runtime exactly like a .NET agent, but whose entire agent loop runs in Python.

**v0.24.** Prerequisites: Python 3.11+, `uv`, Docker (for container builds).

## Overview

A Python agent plugin is a directory containing:

```
langgraph-researcher/
├── plugin.yaml                  # runtime descriptor (kind: agent-handler)
├── pyproject.toml               # Python package metadata
├── Dockerfile.overlay           # bakes the plugin into the runtime image
└── src/langgraph_researcher/
    ├── __init__.py
    ├── server.py                # entrypoint — calls vais_agent_sdk.run(invoke)
    ├── agent.py                 # bridges SDK to the agent loop
    ├── graph.py                 # LangGraph graph or custom loop
    └── state.py                 # Pydantic state model
```

## 1 — Scaffold the package

```bash
mkdir langgraph-researcher && cd langgraph-researcher
uv init --lib src/langgraph_researcher
```

## 2 — Declare the `plugin.yaml`

```yaml
apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: langgraph-researcher
spec:
  runtime: python
  kind: agent-handler                               # v0.24; mcp-tool-server = v0.23 tools
  handler:
    typeName: langgraph_researcher.agent.ResearcherAgent   # must match manifest
  entrypoint: src/langgraph_researcher/server.py
  python:
    version: "3.11"
    interpreter: .venv/bin/python
  health:
    handshakeTimeoutSeconds: 10
    restartPolicy: exponentialBackoff
    invokeTimeoutSeconds: 60                        # cancel slow invokes; subprocess survives
```

`handler.typeName` is the routing key. It must exactly match `spec.handler.typeName` in the agent manifest YAML.

## 3 — Set up `pyproject.toml`

```toml
[project]
name = "langgraph-researcher"
version = "0.1.0"
requires-python = ">=3.11"
dependencies = [
  "vais-agent-sdk >=0.24",
  "langgraph >=0.2",
  "langchain-anthropic >=0.3",
  "pydantic >=2.8,<3",
]

[tool.vais.plugin]
targetApiVersion = "0.24"
kind = "agent-handler"
handlerTypeName = "langgraph_researcher.agent.ResearcherAgent"
```

`targetApiVersion` must be `"0.24"`. The runtime verifies this at handshake time.

## 4 — Define the state model

```python
# src/langgraph_researcher/state.py
from __future__ import annotations
from typing import Optional
from pydantic import BaseModel

class ResearchState(BaseModel):
    user_input: str = ""
    plan: Optional[list[str]] = None
    summary: Optional[str] = None
    turn_count: int = 0

    def to_json(self) -> str:
        return self.model_dump_json()

    @classmethod
    def from_json(cls, blob: str) -> "ResearchState":
        return cls.model_validate_json(blob)
```

The state model must be JSON-serialisable. Use `model_dump_json()` / `model_validate_json()` to round-trip through the opaque blob.

## 5 — Implement the agent loop

Any Python agent framework works. This example uses a LangGraph graph:

```python
# src/langgraph_researcher/graph.py
from langgraph.graph import StateGraph, END
from langgraph_researcher.state import ResearchState

def plan_node(state: ResearchState) -> ResearchState:
    # call LLM, produce plan
    ...

def summarize_node(state: ResearchState) -> ResearchState:
    # call LLM, produce final answer
    ...

def router(state: ResearchState) -> str:
    return "summarize" if state.plan else "plan"

graph = StateGraph(ResearchState)
graph.add_node("plan", plan_node)
graph.add_node("summarize", summarize_node)
graph.add_conditional_edges("plan", router, {"summarize": "summarize", "plan": "plan"})
graph.add_edge("summarize", END)
graph.set_entry_point("plan")
compiled = graph.compile()
```

## 6 — Bridge to `vais-agent-sdk`

```python
# src/langgraph_researcher/agent.py
from vais_agent_sdk import AgentRequest, AgentResponse
from langgraph_researcher.graph import compiled
from langgraph_researcher.state import ResearchState

async def invoke(request: AgentRequest) -> AgentResponse:
    state = ResearchState.from_json(request.state) if request.state else ResearchState()
    state = state.model_copy(update={"user_input": request.user_message})
    result = await compiled.ainvoke(state)
    final: ResearchState = result
    return AgentResponse(
        assistantMessage=final.summary or "No answer produced.",
        newState=final.to_json(),
    )
```

## 7 — Write the server entrypoint

```python
# src/langgraph_researcher/server.py
import os, sys

_src = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _src not in sys.path:
    sys.path.insert(0, _src)

from vais_agent_sdk import run
from langgraph_researcher.agent import invoke

def main() -> None:
    run(invoke)

if __name__ == "__main__":
    main()
```

## 8 — Create a virtual environment and lock

```bash
uv venv --python 3.11
uv sync
```

For production deployment, create a relocatable venv that can be baked into the container:

```bash
uv venv --relocatable .venv-prod
uv sync --python .venv-prod/bin/python
```

## 9 — Write the agent manifest

```yaml
# research-agent.yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: research-agent
  version: "1.0"
spec:
  handler:
    typeName: langgraph_researcher.agent.ResearcherAgent
  protocols:
    - kind: Http
```

`handler.typeName` must match `plugin.yaml`'s `handler.typeName`. No model spec is needed — the Python agent calls its own LLM SDK directly.

## 10 — Deploy

**Local development:**

```bash
# Point the runtime at your plugin directory
export VAIS_PYTHON_PLUGINS_DIRECTORY=/path/to/plugins
# Apply the manifest
vais agent apply -f research-agent.yaml
# Invoke
vais invoke research-agent --input "What is quantum entanglement?"
```

**Container:**

```dockerfile
# Dockerfile.overlay
FROM ghcr.io/vais-agents/runtime:0.24.0-preview
COPY . /var/lib/vais/plugins/langgraph-researcher
```

Build and push:

```bash
docker build -f Dockerfile.overlay -t my-registry/runtime-researcher:latest .
docker push my-registry/runtime-researcher:latest
```

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Plugin status stays `Loading` for >10s | Interpreter path wrong or packages not installed | Run the server manually: `python src/.../server.py`; check stderr |
| `python-agent-handshake-timeout` in startup log | Server takes too long to print first response | Increase `handshakeTimeoutSeconds`; reduce import time |
| `python-agent-invoke-timeout` on requests | Agent loop exceeds `invokeTimeoutSeconds` | Increase limit in `plugin.yaml`; optimise the agent loop |
| `python-agent-state-too-large` | State blob > `MaxAgentStateSizeBytes` (default 1 MiB) | Prune history or reduce state payload; raise `VAIS_PYTHON_AGENT_MAX_STATE_BYTES` |
| `python-agent-handler-collision` | Another plugin (or .NET plugin) already registered the same `typeName` | Ensure `handler.typeName` is globally unique across all plugins |
| `python-agent-invoke-failed` | User code threw an exception | Check Python subprocess stderr in runtime container logs |

## State persistence

State survives:
- **Subprocess crashes and restarts** — the runtime reloads state from Orleans grain storage on the next invoke
- **Grain deactivation / silo drain** — grain reactivates on the next call and restores state
- **Silo relocations** — state comes from Orleans; the new silo's subprocess gets the same blob

State does **not** survive:
- Explicit `Reset()` — agent manifest or `/v1/agents/{id}/reset` call
- Grain `DeleteAsync` — clears grain storage

## Security

Python agents run as the same UID as the runtime process. The plugin directory is trusted — do not mount untrusted code into the plugins directory. Secret injection (env var passthrough from Kubernetes secrets to the subprocess) is planned for v0.24.x.

## See also

- [Polyglot agents concept](../concepts/polyglot-agents.md) — architecture, protocol, state model
- [Package a Python plugin (v0.23)](package-a-python-plugin.md) — tool-level plugins
- [PluginAgentLangGraphResearcher sample](../../samples/PluginAgentLangGraphResearcher/README.md)
- [Problem details URNs — Python agent](../reference/problem-details-urns.md#python-agent-v024-pillar-f)
