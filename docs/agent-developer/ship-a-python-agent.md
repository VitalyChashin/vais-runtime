# Ship a simple Python agent

You'll author an agent's logic in Python, package it as a plugin, and ship it via `vais plugin-push`. The runtime supervises the Python subprocess; durability and state survive subprocess restarts, silo relocations, and pod rolls. End state: an agent whose loop runs entirely in Python, invokable through the same `vais invoke` you used for declarative YAML agents.

## Why Python, not YAML?

Declarative YAML covers most agents. Reach for Python when you need:

- A non-trivial control flow (custom branching, retries on specific failure modes, looped self-correction).
- Tight integration with a Python-only library (LangGraph state graphs, scientific stacks, etc.).
- Custom state that doesn't fit `model` + `systemPrompt` + `tools`.

The plugin contract is uniform across languages — the grain is the durability anchor, the subprocess is ephemeral, state round-trips on every turn.

## Prerequisites

- Python 3.11+ and [`uv`](https://docs.astral.sh/uv/) installed locally.
- Docker.
- A running runtime ([DevOps section](../devops/index.md)).
- The CLI pointed at it.

## Step 1 — Scaffold the package

```bash
mkdir my-py-agent && cd my-py-agent
uv init --lib .
```

The runtime expects this layout:

```
my-py-agent/
├── plugin.yaml                # runtime descriptor (kind: Plugin)
├── pyproject.toml             # Python deps + Vais plugin metadata
└── src/my_py_agent/
    ├── __init__.py
    ├── server.py              # entrypoint — calls vais_agent_sdk.run()
    ├── agent.py               # your invoke function
    └── state.py               # Pydantic state model
```

## Step 2 — Declare `plugin.yaml`

```yaml
apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: my-py-agent
spec:
  runtime: python
  kind: agent-handler
  handler:
    typeName: my_py_agent.agent.SimpleAgent
  entrypoint: src/my_py_agent/server.py
  python:
    version: "3.11"
    interpreter: .venv/bin/python
  health:
    handshakeTimeoutSeconds: 10
    restartPolicy: exponentialBackoff
    invokeTimeoutSeconds: 60
```

`handler.typeName` is an opaque routing key — the runtime uses it to match this plugin subprocess to the agent manifest you write later. It does not need to name an importable Python class; the value in `plugin.yaml` and `agent.yaml` just have to match each other.

## Step 3 — Set up `pyproject.toml`

```toml
[project]
name = "my-py-agent"
version = "0.1.0"
requires-python = ">=3.11"
dependencies = [
  "vais-agent-sdk",          # pre-installed in the runtime image; on PyPI once published
  "pydantic >=2.8,<3",
]

[tool.vais.plugin]
targetApiVersion = "0.24"
kind = "agent-handler"
handlerTypeName = "my_py_agent.agent.SimpleAgent"
```

Until `vais-agent-sdk` is published to PyPI, the runtime image pre-installs it. For local venv builds, point uv at the SDK source:

```bash
# from inside my-py-agent/ — sets up the local dev venv only
uv add --editable /path/to/agentic/samples/python-agent-sdk
```

> **Note:** `uv add --editable` writes a `[tool.uv.sources]` entry that uv uses locally. The runtime's `pip install -e .` during bootstrap ignores `[tool.uv.sources]` and resolves `vais-agent-sdk` from the image's pre-installed system packages instead.

## Step 4 — Define the state model

```python
# src/my_py_agent/state.py
from pydantic import BaseModel

class State(BaseModel):
    turn_count: int = 0
    last_user_message: str = ""

    def to_json(self) -> str:
        return self.model_dump_json()

    @classmethod
    def from_json(cls, blob: str) -> "State":
        return cls.model_validate_json(blob)
```

State must be JSON-serializable — it round-trips through the runtime's grain storage on every turn.

## Step 5 — Implement the invoke function

```python
# src/my_py_agent/agent.py
from vais_agent_sdk import AgentRequest, AgentResponse
from my_py_agent.state import State

async def invoke(request: AgentRequest) -> AgentResponse:
    state = State.from_json(request.state) if request.state else State()
    state.turn_count += 1
    state.last_user_message = request.user_message

    reply = f"Turn {state.turn_count}: you said '{request.user_message}'."

    return AgentResponse(
        assistantMessage=reply,
        newState=state.to_json(),
    )
```

This is the agent loop — read state, do work, return updated state plus an assistant message. The SDK marshals everything over the IP-1 protocol; you don't touch the HTTP layer.

## Step 6 — Write the server entrypoint

```python
# src/my_py_agent/server.py
import os, sys

_src = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _src not in sys.path:
    sys.path.insert(0, _src)

from vais_agent_sdk import run
from my_py_agent.agent import invoke


def main() -> None:
    run(invoke)


if __name__ == "__main__":
    main()
```

## Step 7 — Lock dependencies

```bash
uv venv --python 3.11
uv sync
```

## Step 8 — Write the agent manifest

```yaml
# agent.yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: simple-py
  version: "1.0"
spec:
  handler:
    typeName: my_py_agent.agent.SimpleAgent
  protocols:
    - kind: Http
```

No `model` or `systemPrompt` — those are set by the Python code, not the manifest.

## Step 9 — Deploy and invoke

Python plugins load differently from agent manifests. The runtime discovers plugin subprocesses at startup and via hot-reload — not through `vais apply`. Use two separate commands:

```bash
# Push the Python plugin source. First push bootstraps the venv and starts the
# subprocess (returns 201 Created). Subsequent pushes hot-reload in-place (200 OK).
vais plugin-push my-py-agent

# Register the agent manifest (kind: Agent goes through vais apply as normal)
vais apply -f agent.yaml
```

Then invoke:

```bash
vais invoke simple-py --text "Hello!"
# Turn 1: you said 'Hello!'.

vais invoke simple-py --text "Again."
# Turn 2: you said 'Again.'.
```

The turn counter persists across invocations — state is round-tripping correctly. Kill and restart the runtime container; invoke again — the counter still increments. The grain is the anchor.

## What you built

- A first-class agent whose loop runs entirely in Python.
- State survives subprocess crashes, grain deactivation, and silo relocations.
- Same `vais invoke` operator surface as declarative YAML agents; agent manifest applied with `vais apply` as normal.

## Going further — LangGraph, real LLMs, streaming

The example above is intentionally minimal. The plugin contract supports:

- **LangGraph state graphs** as the agent loop — see the [LangGraph plugin tutorial](../deep-development/build-a-langgraph-plugin.md) in deep development.
- **Real LLM calls** via `request.llm`, the SDK's pre-wired `AsyncLlmClient` that targets the runtime's LLM gateway and carries this invocation's `call_token`, `run_id`, and `traceparent` headers. Direct provider clients (`langchain-openai`, `openai`, `anthropic`, `google-genai`, …) are hard-blocked by the SDK's import guard — the [P12 plugin sandbox contract](../concepts/control-plane.md) requires every LLM call to exit via the runtime gateway so middleware (rate limit, fallback, logging, usage accounting) applies. Configure provider credentials on the runtime's LLM gateway ([Wire the LLM gateway](wire-the-llm-gateway.md)), not on the plugin.
- **Streaming responses** — the SDK supports `vais/agent.stream`; yield text chunks from your invoke function with `stream=True`.

## Next

- **[Compose a multi-agent graph](compose-a-multi-agent-graph.md)** — wire your Python agent and a declarative one into a graph.
- [Full Python agent guide](../guides/package-a-python-agent.md) — secrets, streaming, container builds, troubleshooting.
- [Concepts → Polyglot agents](../concepts/polyglot-agents.md) — protocol, state model, lifecycle.
- [`samples/PluginAgentLangGraphResearcher`](../../samples/PluginAgentLangGraphResearcher/) — a complete LangGraph-backed reference plugin.
