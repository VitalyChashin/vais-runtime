# PluginAgentLangGraphResearcherLive

A live-LLM Python agent-handler packaged as a v0.24 Vais plugin. The handler uses a real `langgraph.StateGraph` with two nodes — `plan` and `summarize` — backed by `langchain-openai.ChatOpenAI` (model: `gpt-4o-mini`). Topology mirrors the hermetic sibling (`PluginAgentLangGraphResearcher`) but makes actual OpenAI API calls. Supports streaming via `vais/agent.stream` (v0.26).

**Concepts:** [polyglot agents](../../docs/concepts/polyglot-agents.md), [polyglot plugins](../../docs/concepts/polyglot-plugins.md), [agent-handler kind](../../docs/concepts/polyglot-plugins.md#agent-handler-kind).  
**Needs API key:** `OPENAI_API_KEY` (must be present in the runtime host environment).  
**Code:** Python + YAML only — no C# required.

---

## Layout

```
PluginAgentLangGraphResearcherLive/
├── Dockerfile                      # full-stack build (runtime from source + plugin)
├── docker-compose.yml              # local dev — docker compose up --build
├── research-agent-live.yaml        # agent manifest (vais apply -f)
└── langgraph-researcher-live/
    ├── plugin.yaml                 # v0.24 agent-handler descriptor
    ├── pyproject.toml              # Python metadata + [tool.vais.plugin]
    ├── Dockerfile.overlay          # overlay onto a published runtime image
    └── src/langgraph_researcher_live/
        ├── agent.py                # ResearcherAgent — vais-agent-sdk bridge
        ├── graph.py                # StateGraph: plan → summarize + router
        ├── server.py               # subprocess entrypoint (vais_agent_sdk.run)
        └── state.py                # ResearchState Pydantic model
```

---

## Quickstart — Docker Compose (recommended)

This path builds the Vais runtime from source and the Python plugin in a single `docker compose up`. No pre-built venv or published runtime image required — works on Windows, macOS, and Linux.

### 1. Set the OpenAI API key

```bash
export OPENAI_API_KEY="sk-..."
```

### 2. Build and start the runtime

```bash
# From repo root:
docker compose -f samples/PluginAgentLangGraphResearcherLive/docker-compose.yml up --build -d
```

First build downloads the .NET SDK, ASP.NET runtime, and uv+Python layers (~2–4 min). Subsequent builds reuse the layer cache.

Verify the runtime is healthy:

```bash
curl http://localhost:8080/healthz   # → ok
```

### 3. Register the agent

```bash
vais config set-context local --server http://localhost:8080
vais apply -f samples/PluginAgentLangGraphResearcherLive/research-agent-live.yaml
```

### 4. Invoke

```bash
vais invoke research-agent-live --text "What is quantum computing?"
```

The runtime invokes the Python subprocess, which runs the LangGraph plan→summarize pipeline and returns the GPT-4o-mini response.

**Streaming (v0.26):** The Python agent supports `vais/agent.stream` — chunks are yielded as the LLM generates and surfaced as `delta` events:

```bash
vais invoke research-agent-live --text "What is quantum computing?" --stream
```

```
turn.started
delta  "Quantum computing harnesses the principles of quantum mechanics..."
delta  " Unlike classical bits, qubits can exist in superposition..."
delta  " This allows quantum computers to explore many solutions simultaneously..."
turn.completed
```

The `.stream` handler in `agent.py` yields tokens from `ChatOpenAI`'s streaming response; the SDK runner collects them and bundles them as `deltas` in the `vais/agent.stream` response. The .NET shim emits `CompletionDelta` events then a terminal `TurnCompleted`.

Multi-turn (state is persisted across calls):

```bash
vais invoke research-agent-live --session-id my-session --text "What is quantum computing?"
vais invoke research-agent-live --session-id my-session --text "Focus on error correction."
```

On the second call the router skips `plan` (plan already exists) and goes directly to `summarize`.

### 5. Stop

```bash
docker compose -f samples/PluginAgentLangGraphResearcherLive/docker-compose.yml down
```

---

## Production — Dockerfile.overlay

To bake the plugin into a published runtime image:

```bash
# From samples/:
docker build \
  -f PluginAgentLangGraphResearcherLive/langgraph-researcher-live/Dockerfile.overlay \
  -t my-registry/vais-researcher-live:0.24.0 .

docker push my-registry/vais-researcher-live:0.24.0
```

The overlay Dockerfile uses `ghcr.io/vais-agents/runtime:0.24.0-preview` as the base and builds the Python venv inside Linux (multi-stage), so it also works from a Windows host.

---

## Graph topology

```
START
  │
  ├─[is_planned=false]──► plan ──► summarize ──► END
  │
  └─[is_planned=true]────────────► summarize ──► END
```

- **plan**: calls `gpt-4o-mini` to decompose the topic into 3 research questions (JSON array).
- **summarize**: calls `gpt-4o-mini` to write a 2–3 paragraph summary covering those questions.
- **router**: checks `ResearchState.is_planned()` — skips planning on turns after the first.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Plugin not scanned | `VAIS_PYTHON_PLUGINS_DIRECTORY` not set | Verify env var in compose/runtime config |
| `handshake-timeout` in logs | Python subprocess crash on startup | Check `OPENAI_API_KEY` is set; run `.venv/bin/python src/langgraph_researcher_live/server.py` locally |
| `abi-mismatch` | `targetApiVersion` mismatch | Ensure `pyproject.toml` has `targetApiVersion = "0.24"` |
| OpenAI 401 errors | Missing or invalid API key | Set `OPENAI_API_KEY` in environment before `docker compose up` |

## See also

- [PluginAgentLangGraphResearcher](../PluginAgentLangGraphResearcher) — hermetic sibling (no API key, CI-safe)
- [Polyglot agents concept](../../docs/concepts/polyglot-agents.md) — architecture, streaming (v0.26), hot-reload (v0.25)
- [Polyglot plugins concept](../../docs/concepts/polyglot-plugins.md)
- [Package a Python agent guide](../../docs/guides/package-a-python-agent.md) — streaming handler pattern
- [Package a Python plugin guide](../../docs/guides/package-a-python-plugin.md)
