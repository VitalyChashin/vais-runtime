# Build a LangGraph plugin

You'll author a Python agent whose loop is a LangGraph state graph, package it as a container plugin, and deploy it via `vais apply`. End state: a plugin called `intent-router` whose two-node LangGraph (`classify → respond`) runs in its own container, supervised by the runtime, invokable through the same `vais invoke` you used for declarative agents.

## When this path?

LangGraph is right when your agent loop needs:

- Explicit state, branching, and conditional routing modeled as a graph rather than embedded in prose prompts.
- Tight integration with the LangChain ecosystem (LangChain tools, callbacks, tracers).
- Iterative loops with checkpoint-and-replay semantics — useful for plan-and-execute, self-correction, reflection.

The plugin contract is uniform across languages — your subprocess is ephemeral, the grain is the durability anchor, opaque state round-trips on every turn.

A larger working example is in [`samples/PluginAgentLangGraphResearcherLive/`](../../samples/PluginAgentLangGraphResearcherLive/). This tutorial builds a smaller plugin from scratch for clearer pedagogy.

## Prerequisites

- Python 3.11+ and [`uv`](https://docs.astral.sh/uv/) installed locally.
- Docker.
- The `agentic` repo checked out locally — `vais-plugin` is not yet published to PyPI and must be installed from source. The Dockerfile in this tutorial copies it directly from the repo.
- A running runtime ([DevOps section](../devops/index.md)) with `OPENAI_API_KEY` set in its environment — the plugin manifest below references it via `secret://env/OPENAI_API_KEY`, which tells the runtime to read its own env and inject the value into the plugin container.

## 1. Scaffold

Create the plugin directory inside your `agentic/` checkout (we use `samples/intent-router/` here so the Dockerfile build context can reach `sdk/python`):

```bash
# From the agentic/ root:
mkdir -p samples/intent-router && cd samples/intent-router
uv init --lib
```

Add the layout:

```
samples/intent-router/
├── plugin.yaml
├── Dockerfile
├── pyproject.toml
└── src/intent_router/
    ├── __init__.py
    ├── server.py
    ├── agent.py
    └── graph.py
```

## 2. `pyproject.toml`

`uv init --lib` generates `[project]` and `[build-system]` sections. Replace only the `[project]` section with the following — leave `[build-system]` intact:

```toml
[project]
name = "intent-router"
version = "0.1.0"
requires-python = ">=3.11"
dependencies = [
  "vais-plugin",
  "langgraph >=0.2",
  "langchain-openai >=0.2",
  "pydantic >=2.8,<3",
]

[build-system]
requires = ["hatchling"]
build-backend = "hatchling.build"
```

## 3. Define the LangGraph state and graph

```python
# src/intent_router/graph.py
from typing import Literal
from langchain_openai import ChatOpenAI
from langgraph.graph import StateGraph, END
from pydantic import BaseModel

class State(BaseModel):
    user_message: str
    intent: Literal["FACTUAL", "CREATIVE", "OPINION", ""] = ""
    reply: str = ""

_llm = ChatOpenAI(model="gpt-4o-mini", temperature=0)

async def classify(state: State) -> State:
    out = await _llm.ainvoke(
        f"Classify the following message as exactly one of: FACTUAL, CREATIVE, OPINION. "
        f"Reply with the single word. Message: {state.user_message}"
    )
    intent = out.content.strip().upper()
    if intent not in {"FACTUAL", "CREATIVE", "OPINION"}:
        intent = "FACTUAL"
    return state.model_copy(update={"intent": intent})

async def respond(state: State) -> State:
    style = {
        "FACTUAL":  "Answer concisely and accurately.",
        "CREATIVE": "Answer imaginatively.",
        "OPINION":  "Acknowledge the opinion-laden nature; give a balanced view.",
    }[state.intent]
    out = await _llm.ainvoke(f"{style} Question: {state.user_message}")
    return state.model_copy(update={"reply": out.content})

def build_graph():
    g = StateGraph(State)
    g.add_node("classify", classify)
    g.add_node("respond", respond)
    g.set_entry_point("classify")
    g.add_edge("classify", "respond")
    g.add_edge("respond", END)
    return g.compile()
```

Two nodes. The first calls the model to label intent; the second tailors the response style to that label. Linear edges keep the tutorial simple — LangGraph's real power is conditional edges (`add_conditional_edges`), checkpointing, and parallel execution; once this graph runs, those extensions are localized to `graph.py`.

## 4. Bridge to the plugin SDK

```python
# src/intent_router/agent.py
from vais_plugin import InvokeRequest, InvokeResponse, PluginAgent, vais_plugin
from intent_router.graph import State, build_graph

_graph = build_graph()

@vais_plugin("0.24")
class IntentRouter(PluginAgent):
    async def invoke(self, request: InvokeRequest) -> InvokeResponse:
        user_text = next(
            (m.content or "" for m in reversed(request.messages) if m.role == "user"),
            "",
        )
        result = await _graph.ainvoke(State(user_message=user_text))
        return InvokeResponse(assistant_message=result["reply"])
```

The SDK marshals the IP-1 protocol — request comes in with `messages` + opaque state; you return an `InvokeResponse`. LangGraph's compiled graph handles the actual flow.

## 5. Entrypoint

```python
# src/intent_router/server.py
from intent_router.agent import IntentRouter

if __name__ == "__main__":
    IntentRouter().serve()
```

## 6. Dockerfile

Because `vais-plugin` is not yet on PyPI, the Dockerfile copies it from the repo before installing the plugin package. The build context is set to the `agentic/` root in `plugin.yaml` so that `sdk/python` is reachable:

```dockerfile
FROM python:3.12-slim
WORKDIR /app
COPY sdk/python /sdk/vais-plugin
COPY samples/intent-router/pyproject.toml ./
COPY samples/intent-router/src/intent_router /app/intent_router
RUN pip install --no-cache-dir /sdk/vais-plugin && pip install --no-cache-dir .
USER 65532:65532
EXPOSE 8080
ENV PYTHONPATH=/app
CMD ["python", "-m", "intent_router.server"]
```

The non-root user, read-only filesystem expectations, and dropped capabilities are enforced by the runtime's `DockerContainerSupervisor` when it starts the plugin — see the [P12 plugin sandbox contract](../concepts/control-plane.md) for the full hardening list.

## 7. `plugin.yaml`

```yaml
apiVersion: vais.agents/v1
kind: ContainerPlugin
metadata:
  id: intent-router
  version: "1.0"
  description: LangGraph classify → respond two-node plugin.
spec:
  image: intent-router:1.0
  port: 8080
  build:
    context: ../../
    dockerfile: samples/intent-router/Dockerfile
  secrets:
    OPENAI_API_KEY: secret://env/OPENAI_API_KEY
```

`spec.secrets` is a map of env-var name (inside the plugin container) → `secret://` URI. The `secret://env/OPENAI_API_KEY` URI tells the runtime to read `OPENAI_API_KEY` from its own environment and inject it as `OPENAI_API_KEY` in the plugin container at startup. The runtime never persists the value.

`vais apply` reads `spec.build`, builds the image locally (only if the tag doesn't exist), and registers the plugin in one shot.

## 8. Apply and invoke

```bash
# Confirm OPENAI_API_KEY is set in the runtime container's environment.
# (Set at runtime startup — see the DevOps section. The secret reference
# in plugin.yaml resolves against this.)

# Build + register the plugin
vais apply -f plugin.yaml
# intent-router:1.0 built ✓
# intent-router created (container-plugin, version 1.0)

vais plugin-status
# NAME            KIND        TOPOLOGY    STATE   IMAGE              REPLICAS   API VERSION   HANDLERS/TOOLS   PID
# intent-router   Container   standalone  Ready   intent-router:1.0  1          0.24          1/1              -
```

Apply an agent manifest:

```yaml
# agent.yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: router
  version: "1.0"
spec:
  handler:
    typeName: intent_router.agent.IntentRouter
  protocols:
    - kind: Http
```

```bash
vais apply -f agent.yaml

vais invoke router --text "What is the speed of light?"
# (FACTUAL routing → concise answer)

vais invoke router --text "Write me a haiku about Mars."
# (CREATIVE routing → imaginative answer)
```

The runtime's `ContainerAgentShim` translates each invocation into a `POST /v1/invoke` against the plugin container. The container loads, runs the LangGraph, returns. Opaque state round-trips on every turn — the plugin can persist arbitrary JSON across the conversation.

## 9. Iterate

Edit `graph.py` (e.g., add a third intent class), bump `spec.image` in `plugin.yaml` to `:1.1`, and re-apply:

```bash
# plugin.yaml: spec.image: intent-router:1.1
vais apply -f plugin.yaml
# → builds :1.1, PATCHes the runtime, drain-replace happens
```

The runtime drains in-flight `/v1/invoke` calls for this plugin, replaces the container, waits for `/health`, and resumes. The .NET silo and Orleans grain state are untouched.

## What you built

- A Python agent whose loop is a real LangGraph state graph (`classify → respond`).
- A container plugin packaged via Dockerfile and registered through `vais apply`.
- Hot replacement on each image bump — no runtime restart, conversation state preserved.

## Going further

- **Conditional edges.** Replace `add_edge` with `add_conditional_edges(node, router_fn, {...})` to branch dynamically per state.
- **Checkpointing.** LangGraph supports its own checkpointing — fine for transient mid-graph state, but the runtime's grain is the source of truth across turns. Don't double-persist; round-trip critical state through the plugin's `opaqueState` blob.
- **Real tools.** Add LangChain tools to your nodes; route them through the runtime's MCP gateway by calling `request.tool_gateway_url` instead of contacting MCP servers directly. See the [P12 plugin sandbox contract](../concepts/control-plane.md) for why.

## Next

- **[Author a container plugin in Go](author-a-container-plugin-in-go.md)** — same model, language-neutral protocol, no SDK required.
- [Concepts → Polyglot agents](../concepts/polyglot-agents.md) — protocol, state model, lifecycle.
- [`samples/PluginAgentLangGraphResearcherLive/`](../../samples/PluginAgentLangGraphResearcherLive/) — a more substantial reference plugin doing live research with Tavily MCP search.
- [Full Python agent guide](../guides/package-a-python-agent.md) — depth: secrets, streaming, hot reload, troubleshooting.
