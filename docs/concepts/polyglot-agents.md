# Polyglot agents

**v0.24.** Python agents — authored and executed entirely in Python — are first-class citizens of the VAIS runtime: Orleans-backed durability, CLI-deployable, registry-activated, policy/audit/journal wired. This extends v0.23's Python tool plugin model from the tool level to the agent level.

## Three-way comparison

| | External A2A agent | Python tool plugin (v0.23) | Python agent plugin (v0.24) |
|---|---|---|---|
| **Where the agent runs** | External process or service | .NET runtime (Python provides tools) | Python subprocess |
| **Where the LLM loop runs** | External | .NET (`StatefulAiAgent`) | Python (LangGraph, LangChain, custom) |
| **Orleans durability** | No — agent owns its own persistence | Yes — history in grain state | Yes — history + opaque state in grain state |
| **`vais agent apply`** | Yes, via A2A manifest | Yes, declarative | Yes, declarative |
| **Policy / audit / journal** | Partial (invocation boundary only) | Full | Full |
| **Graph nodes** | Yes, via A2A node type | Yes, as tool-calling agent | Yes, as agent node |
| **Streaming** | If the external agent supports it | Yes | Not in v0.24 (request/response only) |
| **Best for** | Federating with third-party or team-owned agents that live outside the runtime | Python domain logic / numeric code called as tools from .NET agents | LangGraph, LangChain, or custom Python agent loops that must be managed identically to .NET agents |

## Why A2A is not enough for managed Python agents

An A2A endpoint federates with external systems. The runtime can *call* an A2A agent but cannot *own* it: the agent has its own persistence, its own activation lifecycle, and its own policy surface. For a Python agent that an operator wants to deploy, scale, audit, and govern alongside .NET agents — from the same `plugin.yaml` + manifest YAML — A2A adds indirection without adding control. v0.24 fills that gap.

## Architecture

```
┌─────────────────────────── .NET runtime ────────────────────────────────────┐
│                                                                              │
│   AiAgentGrain                                                               │
│   ├── OnActivateAsync: supplies PythonAgentShim as IAiAgent                  │
│   ├── AskAsync: agent.AskAsync → persists History + OpaqueState to grain    │
│   └── ResetAsync: agent.Reset → clears both                                 │
│                                                                              │
│   PythonAgentShim : IAiAgent, IOpaqueStateCarrier                           │
│   ├── AskAsync(userMessage)                                                  │
│   │   1. Append ChatTurn(user) to session                                    │
│   │   2. InvokeAgentAsync → vais/agent.invoke over stdio                    │
│   │   3. Guard state size (> MaxAgentStateSizeBytes → fail)                  │
│   │   4. Append ChatTurn(assistant) to session                               │
│   │   5. Update _opaqueState                                                 │
│   │   → return AssistantMessage                                              │
│   └── Reset(): _opaqueState = null; Session.Reset()                         │
│                                                                              │
│        ↕  JSON-RPC 2.0 over stdio (newline-delimited)                        │
│                                                                              │
│   ┌──── Python subprocess (one per plugin per silo) ────────────────────┐   │
│   │   vais-agent-sdk  ──→  user's invoke(request) coroutine             │   │
│   │                         e.g. LangGraph graph.invoke(state)           │   │
│   └──────────────────────────────────────────────────────────────────────┘   │
│                                                                              │
│   Grain storage (Orleans):  History + OpaqueState + SystemPrompt            │
│   Survives: silo restart, grain deactivation, silo relocation               │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Wire protocol

`vais/agent.invoke` and `vais/agent.reset` are custom JSON-RPC 2.0 methods sent over the same MCP stdio channel that v0.23 tool plugins use. The framing is newline-delimited JSON (one object per line, no Content-Length headers).

**Request** (`vais/agent.invoke`):
```json
{
  "jsonrpc": "2.0",
  "id": "550e8400-...",
  "method": "vais/agent.invoke",
  "params": {
    "agentId":     "research-agent",
    "sessionId":   "user-42-session-7",
    "userMessage": "What is quantum entanglement?",
    "state":       null,
    "timeoutSeconds": 60,
    "context":     null
  }
}
```

**Response**:
```json
{
  "jsonrpc": "2.0",
  "id": "550e8400-...",
  "result": {
    "assistantMessage": "Quantum entanglement is...",
    "newState": "{\"step\": 2, \"plan\": [...]}",
    "usage":   [{"model": "claude-haiku-4-5", "inputTokens": 120, "outputTokens": 45}],
    "journal": null
  }
}
```

The subprocess also handles `initialize` and `tools/list` (returning an empty tool list for pure agent plugins) so the v0.23 MCP handshake machinery can proceed unchanged.

## State model

Python agents are stateless per call. The .NET runtime supplies the previous turn's opaque state blob on each invoke and persists the returned blob into Orleans grain storage.

```
Turn 1:  state = null         → Python runs, returns newState = '{"step":1,...}'
Turn 2:  state = '{"step":1}'  → Python loads from state, returns newState = '{"step":2,...}'
Restart: grain reactivates, restores OpaqueState from Orleans, passes to Python on next turn
```

The blob is opaque to the runtime — it is stored and returned verbatim. The Python agent decides its schema (LangGraph `TypedDict`, Pydantic model, raw JSON, etc.).

**Size cap.** The blob is capped at `MaxAgentStateSizeBytes` (default 1 MiB, configurable via `VAIS_PYTHON_AGENT_MAX_STATE_BYTES`). Exceeding the limit fails the invocation with URN `urn:vais-agents:python-agent-state-too-large`; the previous state is left unchanged.

## plugin.yaml — agent-handler kind

Extend a v0.23 `plugin.yaml` with two new fields:

```yaml
apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: langgraph-researcher
spec:
  runtime: python
  kind: agent-handler            # NEW v0.24; absent or "mcp-tool-server" = v0.23 behaviour
  handler:                       # NEW v0.24; required when kind: agent-handler
    typeName: langgraph_researcher.agent.ResearcherAgent   # matches manifest Handler.TypeName
  entrypoint: src/langgraph_researcher/server.py
  python:
    version: "3.11"
    interpreter: .venv/bin/python
  health:
    handshakeTimeoutSeconds: 10
    restartPolicy: exponentialBackoff
    invokeTimeoutSeconds: 60     # NEW v0.24; default 60
```

`handler.typeName` must exactly match the `spec.handler.typeName` in the agent manifest — the registry uses it to route grain activations.

## Agent manifest

No schema change from v0.17. Use `spec.handler.typeName` to point at the Python agent:

```yaml
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

## Python-side SDK

`vais-agent-sdk` (`samples/python-agent-sdk/`) provides the JSON-RPC dispatcher. Plugin authors implement one async function:

```python
from vais_agent_sdk import AgentRequest, AgentResponse, run

async def invoke(request: AgentRequest) -> AgentResponse:
    # Load state
    state = MyState.model_validate_json(request.state) if request.state else MyState()
    # Run the agent loop (LangGraph, LangChain, custom)
    state = await my_graph.ainvoke(state | {"user_input": request.user_message})
    return AgentResponse(
        assistantMessage=state["output"],
        newState=state.model_dump_json(),
    )

if __name__ == "__main__":
    run(invoke)
```

`run()` handles:
- `initialize` + `tools/list` stubs (MCP handshake compatibility)
- `vais/agent.invoke` dispatch → `invoke(request)`
- `vais/agent.reset` dispatch → optional `on_reset(session_id)` hook
- Exception handling: user errors become JSON-RPC error responses with `urn:vais-agents:python-agent-invoke-failed`; the subprocess survives for the next call

## Subprocess lifecycle

One subprocess is spawned per plugin per silo at startup — not per session. Multiple concurrent sessions multiplex over the same stdio channel. The `vais/agent.invoke` call carries `sessionId` so the Python side can maintain per-session in-memory state if desired (the opaque blob handles cross-turn persistence from the .NET side).

**Invoke timeout.** If a `vais/agent.invoke` call does not complete within `invokeTimeoutSeconds`, the call is cancelled and `urn:vais-agents:python-agent-invoke-timeout` is returned. The subprocess is **not** killed; it continues serving other sessions. Only an unrecoverable subprocess exit triggers the restart policy.

**Restart policy.** Same as v0.23 (`exponentialBackoff` | `never`). After restart, the next invoke will receive the last persisted state from Orleans, so Python's in-memory per-session state is rebuilt from the blob on the first call of that session post-restart.

## Durability across failures

| Scenario | Effect on opaque state |
|---|---|
| Subprocess crashes and restarts | Preserved — next invoke loads from Orleans grain state |
| Grain deactivates (idle timeout, silo drain) | Preserved — grain reactivates on next call, loads OpaqueState |
| Silo restarts | Preserved — grain reactivates on another silo, loads from Orleans storage |
| Grain reset (`ResetAsync`) | Cleared — both History and OpaqueState set to null in grain state |

## Error URNs

See [Problem details URNs — Python agent](../reference/problem-details-urns.md#python-agent-v024-pillar-f).

## Limitations in v0.24

- **No streaming.** `IStreamingAiAgent` is not implemented. Consumers asking for streaming get a single-event fallback (the non-streaming response wrapped in a stream event). v0.24.x will add streaming support.
- **No hot-reload.** Python agent plugins are loaded at startup. Changing the subprocess binary requires a silo restart. v0.24.x will extend v0.22's hot-reload machinery.
- **Single subprocess per plugin per silo.** Multiple sessions share one process. High-concurrency plugins should design their `invoke` function to be re-entrant or use asyncio concurrency internally.
- **Python-side tool calls are opaque.** Tool calls made inside the Python agent loop are internal; only aggregate `usage` and optional `journal_entries` cross the boundary. Per-tool-call events are v0.24.x.

## See also

- [Package a Python agent](../guides/package-a-python-agent.md) — step-by-step guide
- [Polyglot plugins (v0.23)](polyglot-plugins.md) — tool-level Python plugins
- [Runtime plugins](runtime-plugins.md) — the v0.18 .NET plugin baseline
- [PluginAgentLangGraphResearcher sample](../../samples/PluginAgentLangGraphResearcher/README.md)
