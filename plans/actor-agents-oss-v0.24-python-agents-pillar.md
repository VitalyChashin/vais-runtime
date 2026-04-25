# v0.24.0-preview — First-class Python agents (polyglot agents pillar)

Tactical plan for v0.24 — let partners drop a **Python agent** (LangGraph, LangChain, custom loop) under `/var/lib/vais/plugins/` and have the runtime treat it as a **first-class VAIS agent**: Orleans-backed durability, CLI-deployable, registry-activated, single-writer-per-session, policy/audit/journal wired. Extends v0.23's Python-plugin subprocess machinery from tool-level to agent-level. Created 2026-04-24.

---

## Why this pillar exists

v0.23 shipped Python *tool* plugins: the Python side exposes an MCP tool surface; the .NET runtime owns the agent loop. That works for NumPy/pandas tool code but **can't host a LangGraph-style agent** — LangGraph is a state machine with routing decisions, which MCP has no vocabulary for.

v0.20/v0.21 shipped **A2A** for cross-runtime agent invocation. An A2A endpoint is *external integration*: the agent lives in its own process with its own persistence, the runtime can only invoke it. **A2A does not make a Python agent a first-class citizen of the VAIS runtime** — Orleans grain storage, the manifest registry, `vais agent apply`, checkpointing, cross-silo activation, the journal, graph checkpointing, OPA policy — none of that wraps an A2A endpoint. A2A is the right answer for federating with third-party agents; it **breaks the "Kubernetes for agents" concept** for agents meant to be managed alongside .NET ones.

The gap v0.24 fills: **a Python agent authoring surface that is first-class** — authored in Python, executed in Python, but durability, activation, and control-plane integration owned by the .NET runtime exactly as for a native `IAiAgent`.

---

## Scope

**MVP boundary locked 2026-04-24**:

1. **Package extension, not new package.** Extend `Vais.Agents.Runtime.Plugins.Python` (v0.23) with an agent-handler path. The subprocess supervisor, scanner, plugin.yaml schema, and lifecycle story from v0.23 are reused intact. `IsPackable=true`, same dependencies.
2. **Handler-level boundary.** A Python plugin provides an `IAiAgent` equivalent — the full agent loop runs in Python. v0.23's tool-level boundary and v0.24's agent-level boundary coexist in the same directory layout; a single plugin.yaml declares which kind it provides.
3. **Subprocess reuse, not shared.** One long-lived Python subprocess per plugin per silo, spawned by the existing `PythonSubprocessSupervisor` (v0.23). Agent invocations multiplex over the same stdio channel the MCP handshake already uses. Not per-grain (too expensive).
4. **Wire protocol = MCP-stdio framing + VAIS-namespaced methods.** Reuses the JSON-RPC 2.0 over stdio framing from the MCP SDK (both sides). New methods live under the `vais/agent.*` namespace: `vais/agent.invoke`, `vais/agent.reset`, `vais/agent.getState`. Tool-provider plugins keep using `tools/list` + `tools/call`. An all-in-one plugin can support both namespaces on the same subprocess.
5. **Orleans owns durability; Python is stateless per call.** Each `vais/agent.invoke` carries `(session_id, user_message, opaque_state_blob)`; returns `(assistant_message, new_opaque_state_blob, journal_entries, usage)`. The .NET shim persists `new_opaque_state_blob` into Orleans grain storage keyed by `(agent_id, session_id)`. Subprocess restart does not lose per-session state; next invoke reloads from storage.
6. **ChatTurn history double-tracked.** The opaque state is the LangGraph (or whatever) internal state — only the Python side interprets it. In parallel, the shim appends `user` and `assistant` `ChatTurn`s to the existing `IAgentSession.History` so audit, policy, and `/v1/agents/{id}/history` keep working identically to .NET agents.
7. **Manifest shape unchanged.** Agent manifest still carries `Handler.TypeName`. v0.24 adds a new plugin-level registration (`IAgentHandlerFactory` variant) so the existing translator plugin branch (`AgentManifestTranslator.cs:83-121`) dispatches to a Python-shim factory without schema changes. The Python-agent plugin's `plugin.yaml` declares a `handler.typeName` field that the factory registers under.
8. **Python-side SDK as a thin helper.** A `vais-agent-sdk` PyPI package (or in-repo module initially) that: reads framed JSON-RPC off stdin, calls a user-provided `async def invoke(request) -> response`, writes framed response to stdout. Plugin authors implement one async function — no protocol glue. LangGraph/LangChain authors wire their graph inside that function.
9. **Five new URNs** in extended `PythonPluginUrns`: `python-agent-invoke-failed`, `python-agent-invoke-timeout`, `python-agent-state-too-large`, `python-agent-protocol-error`, `python-agent-handler-collision` (when a Python `handler.typeName` collides with an already-registered .NET handler).
10. **State-size cap** (configurable, default 1 MiB per session). Exceed → fail the invocation with `python-agent-state-too-large` URN; session state left unchanged. Protects Orleans storage and wire budget.
11. **Invocation timeout** per `vais/agent.invoke` — default 60s, per-plugin override via `plugin.yaml` `health.invokeTimeoutSeconds`. Exceed → cancel the stdio call, subprocess **not** killed (the supervisor's crash restart is too heavy-handed for a hung invoke); the plugin goes back to Ready for the next call. If the same plugin times out 3× consecutively, escalate to subprocess restart (supervisor hook).
12. **Python-side streaming = out of scope for v0.24.** The invoke surface is request/response; interactive mid-turn events (token streaming, progress) defer to v0.24.x. `IStreamingAiAgent` is **not implemented** by the shim; consumers that ask for streaming against a Python agent get the non-streaming fallback from the existing `StatefulAiAgent` façade (it degrades cleanly today).
13. **Trusted-plugin security posture**, unchanged from v0.23. Python subprocess runs as the same UID as the runtime; no new attack surface.

### Explicitly deferred to post-v0.24

- **Python-side streaming** — `IStreamingAiAgent` + mid-invoke event notifications. The protocol can support JSON-RPC notifications back from Python; implementing them is v0.24.x polish.
- **Graph-node Python agents as local nodes.** v0.24 handles individual agents; a Python-hosted agent can be a node in a VAIS graph via its manifest, but graph-level checkpointing through Python is end-of-pillar follow-up (requires state-blob coordination with `IGraphCheckpointer`).
- **Non-Python agent runtimes** (Node, Go, Rust). Protocol is language-agnostic; SDKs per language are a follow-up.
- **Python-side `ICompletionProvider` re-use.** Python authors call their own provider SDKs (OpenAI, Anthropic). Sharing the .NET provider pool would require a reverse protocol (.NET calls back into .NET from Python mid-invoke) — deferred.
- **Per-invoke tool-call visibility.** Python's tool calls inside an invoke are internal to Python; only aggregate `usage` and optional `journal_entries` cross the boundary. Fine-grained `ToolCallStarted` / `ToolCallCompleted` events are v0.24.x.
- **Hot-reload of Python agents.** v0.22 .NET hot-reload isn't wired for Python subprocesses yet. Apply to v0.24.x alongside Python tool-plugin hot-reload.
- **Multi-instance plugin scaling.** Single subprocess per plugin per silo in v1, matching v0.23.

---

## Design questions — resolved

| # | Question | Decision |
|---|---|---|
| 1 | Where does Python's agent loop run? | In the Python subprocess, owned by v0.23's supervisor |
| 2 | How is state durable? | Opaque JSON blob round-tripped per invoke; persisted via Orleans grain storage keyed on `(agentId, sessionId)` |
| 3 | What wire framing? | MCP's JSON-RPC-2.0 over stdio framing, reusing existing SDK framing both sides; methods live under `vais/agent.*` namespace |
| 4 | Same subprocess as tools? | Yes — one supervisor, one stdio channel; method namespace discriminates |
| 5 | Shim `IAiAgent` implementation lives where? | New `PythonAgentShim : IAiAgent` in `Runtime.Plugins.Python`; registered as `IAgentHandlerFactory` at plugin load |
| 6 | Chat history: Python or .NET? | Both — .NET tracks `ChatTurn` for audit/policy; Python tracks LangGraph's richer state in opaque blob |
| 7 | Streaming in v0.24? | Out of scope — non-streaming invoke only; streaming deferred to v0.24.x |
| 8 | Timeout vs. restart | Invoke timeout ≠ subprocess death; only 3× consecutive timeouts escalate to restart |
| 9 | State size cap | Configurable, default 1 MiB; fail with URN, leave state unchanged on exceed |
| 10 | Manifest schema change? | None — existing `Handler.TypeName` seam sufficient |
| 11 | CLI deployment | No CLI change — `vais plugin install` already deploys arbitrary plugin folders; only the `handler.kind` field in plugin.yaml is new |

---

## Proposed PR shape

Three-PR sequence inside v0.24. Each independently shippable.

### PR 1 — Wire protocol + shim IAiAgent + translator dispatch

- [ ] Extend `PluginYamlDeserializer.PluginYamlSpec` with a new `handler` section:
  ```yaml
  spec:
    runtime: python
    kind: agent-handler    # NEW — one of "mcp-tool-server" (v0.23 default) | "agent-handler"
    handler:               # present only when kind == agent-handler
      typeName: langgraph_researcher.agent.ResearcherAgent
    entrypoint: src/researcher/server.py
    python: { interpreter: .venv/bin/python }
    health: { handshakeTimeoutSeconds: 5, restartPolicy: exponentialBackoff, invokeTimeoutSeconds: 60 }
  ```
  Absent `kind` = `mcp-tool-server` (v0.23 compatibility).
- [ ] `PythonPluginDescriptor` gains `HandlerKind` (enum: `McpToolServer` | `AgentHandler`), `HandlerTypeName` (nullable), `InvokeTimeoutSeconds`.
- [ ] New wire protocol types in `Vais.Agents.Runtime.Plugins.Python` (internal for v0.24; promote to Abstractions in v0.24.x after shape settles):
  ```csharp
  internal sealed record AgentInvokeRequest(
      string AgentId,
      string SessionId,
      string UserMessage,
      string? State,                  // opaque JSON, null on first turn
      int TimeoutSeconds,
      IReadOnlyDictionary<string,string>? Context);   // trace_id, tenant_id, etc.

  internal sealed record AgentInvokeResponse(
      string AssistantMessage,
      string? NewState,               // opaque JSON
      IReadOnlyList<AgentInvokeUsage>? Usage,
      IReadOnlyList<AgentInvokeJournalEntry>? Journal);

  internal sealed record AgentInvokeUsage(string ProviderId, int InputTokens, int OutputTokens);
  internal sealed record AgentInvokeJournalEntry(string Type, string Payload);   // opaque pass-through to JournalEntry
  ```
- [ ] `PythonAgentShim : IAiAgent` — the core new type. One instance per `(agentId, sessionId)` — lives inside the activated `AiAgentGrain`, so Orleans already enforces single-writer per session. Constructor takes `(PythonSubprocessSupervisor supervisor, string handlerTypeName, IAgentSession session, IStateStore stateStore)`.
  - `AskAsync(userMessage, ct)`:
    1. Load `State` from `IStateStore` for `(agentId, sessionId)`.
    2. Build `AgentInvokeRequest` with current state + trace/tenant context from `IAgentContextAccessor`.
    3. `supervisor.InvokeAsync("vais/agent.invoke", request, invokeTimeout, ct)` — sends JSON-RPC over existing stdio channel; returns `AgentInvokeResponse`.
    4. Append `ChatTurn(user, userMessage)` + `ChatTurn(assistant, response.AssistantMessage)` to `Session`.
    5. Persist `response.NewState` via `IStateStore.SaveAsync(agentId, sessionId, newState)`.
    6. Forward `response.Journal` entries to the existing `IJournalWriter` (so v0.20 journal replay works uniformly).
    7. Emit `response.Usage` on existing usage metrics.
    8. Return `response.AssistantMessage`.
  - `Reset()`: calls `vais/agent.reset` against the subprocess (Python clears its in-memory cache for this session); clears `IStateStore` entry; resets `Session`.
  - `SystemPrompt` get/set: pass through in the request `Context` dictionary; Python side decides whether to honour it.
- [ ] `IStateStore` new interface in `Vais.Agents.Runtime.Plugins.Python`:
  ```csharp
  internal interface IPythonAgentStateStore
  {
      ValueTask<string?> LoadAsync(string agentId, string sessionId, CancellationToken ct);
      ValueTask SaveAsync(string agentId, string sessionId, string state, CancellationToken ct);
      ValueTask DeleteAsync(string agentId, string sessionId, CancellationToken ct);
  }
  ```
  Default implementation `OrleansPythonAgentStateStore` in `Vais.Agents.Hosting.Orleans` — backed by the same grain-storage provider the declarative path uses. Registered via `AddOrleansPythonAgentStateStore()`. In-memory fallback for unit tests.
- [ ] `PythonAgentShimFactory : IAgentHandlerFactory` — created per-plugin at load time, matches on `handler.typeName`. `CreateAsync(manifest, sp, ct)` resolves the supervisor + state store + session from DI and constructs `PythonAgentShim`.
- [ ] `PythonPluginHostService` (existing v0.23) gains a `RegisterAgentHandlers(IPluginHandlerRegistry)` method — called after startup handshake completes, registers one `PythonAgentShimFactory` per loaded plugin with `handlerKind == AgentHandler`. Collision with an existing .NET handler → `python-agent-handler-collision` URN; plugin marked Unavailable.
- [ ] `PythonSubprocessSupervisor` gains `Task<TResponse> InvokeAsync<TRequest, TResponse>(string method, TRequest, TimeSpan timeout, CancellationToken)` — generic JSON-RPC call on the existing McpClient's transport. Timeout cancels the specific call without killing the subprocess. Three consecutive timeouts on the same plugin → restart supervisor state machine (`RequestRestart()`).
- [ ] Five new URNs in `PythonPluginUrns`:
  - `python-agent-invoke-failed`
  - `python-agent-invoke-timeout`
  - `python-agent-state-too-large`
  - `python-agent-protocol-error` (malformed JSON, missing required fields, etc.)
  - `python-agent-handler-collision`
- [ ] Unit tests (no Python required; mock `ISubprocessHandle`):
  - `AskAsync` round-trips state: initial `null` → first call → response has new state → second call sends the same state.
  - `AskAsync` appends both user and assistant ChatTurns to `IAgentSession`.
  - `Reset` clears state store + session + sends `vais/agent.reset`.
  - Invoke timeout → `python-agent-invoke-timeout` URN; subprocess **not** killed; supervisor still Ready.
  - State size > 1 MiB on response → `python-agent-state-too-large`; previous state unchanged.
  - Handler-collision: registering a Python handler with the same `typeName` as a loaded .NET plugin → collision URN; Python plugin marked Unavailable.
  - Protocol error (malformed JSON from Python) → `python-agent-protocol-error`; state unchanged.
- [ ] `PublicAPI.Unshipped.txt`: none (all new types internal for v0.24; DI extensions public).
- [ ] Full solution builds 0 warnings / 0 errors; new loader tests green; all v0.23 tests still green (no regression).

### PR 2 — Python-side SDK + composition root + sample

- [ ] New Python package in the repo: `samples/python-agent-sdk/` — `pyproject.toml` with `project.name = "vais-agent-sdk"`, version pinned to v0.24. Minimal runtime deps (just `pydantic` for request/response validation; no LangGraph dep — that's the plugin author's choice). Ships source-only; published to PyPI post-tag is a v0.24.x task.
- [ ] SDK surface (one module, `vais_agent_sdk`):
  ```python
  from vais_agent_sdk import AgentRequest, AgentResponse, run

  async def invoke(request: AgentRequest) -> AgentResponse:
      # user's LangGraph / LangChain / custom loop here
      ...

  if __name__ == "__main__":
      run(invoke)   # reads JSON-RPC framed stdin, calls invoke, writes framed stdout
  ```
  `run()` implements:
  - JSON-RPC 2.0 framing over stdin/stdout (length-prefixed or newline-delimited; match what PR 1 chose — aligning with MCP SDK's framing decision).
  - Dispatch `vais/agent.invoke` → `invoke(request)` coroutine.
  - Dispatch `vais/agent.reset` → user-optional `on_reset(session_id)` hook; default no-op.
  - Dispatch `vais/agent.getState` → debug echo (returns last known state for session; optional).
  - `initialize` + `tools/list` stubs for compatibility (empty tool list — a pure agent plugin doesn't advertise MCP tools).
  - Exception in user code → JSON-RPC error response with `python-agent-invoke-failed` URN; no process exit (survives for next call).
- [ ] SDK unit tests (`samples/python-agent-sdk/tests/`): pytest, mocks stdin/stdout, verifies framing, dispatch, error handling.
- [ ] `Vais.Agents.Runtime.Host.CompositionRoot.ConfigureServices` addition (single block, replaces nothing — additive):
  ```csharp
  if (!string.IsNullOrWhiteSpace(options.PythonPluginsDirectory))
  {
      services.AddPythonPlugins(new PythonPluginLoaderOptions
      {
          PluginsDirectory = options.PythonPluginsDirectory,
      });
      services.AddOrleansPythonAgentStateStore();   // NEW v0.24
  }
  ```
- [ ] New sample `samples/PluginAgentLangGraphResearcher/` — hermetic LangGraph-style agent (no real LLM calls in the sample; heuristic state machine that mimics a two-node LangGraph):
  - `plugin.yaml` with `kind: agent-handler`, `handler.typeName: langgraph_researcher.agent.ResearcherAgent`.
  - `pyproject.toml`, `uv.lock`, `Dockerfile.overlay`.
  - `src/langgraph_researcher/{__init__.py,server.py,agent.py,graph.py,state.py}`.
  - `graph.py` — two nodes (`plan`, `summarize`) with a router edge, state is a `TypedDict`. Heuristic — no real LLM to keep hermetic.
  - `agent.py` — calls `graph.invoke(state | {"user_input": …})` and extracts the final message.
  - `server.py` — `vais-agent-sdk`'s `run(invoke)` entrypoint.
  - `research-agent.yaml` — sample agent manifest with `Handler.TypeName: langgraph_researcher.agent.ResearcherAgent`.
  - `README.md` — build + package + deploy walkthrough + `vais agent apply -f research-agent.yaml && vais invoke research-agent --input "…"`.
- [ ] Integration test project `tests/Vais.Agents.Runtime.Host.PythonAgents.Tests/` (gated by `VAIS_RUN_PYTHON_PLUGIN_TESTS=1`, matching v0.23):
  - **Golden path**: apply `research-agent.yaml`, invoke twice, confirm state persisted between turns (second invoke reflects first turn's state).
  - **Subprocess restart mid-session**: invoke once, force subprocess restart (`kill -TERM` the PID), invoke again — state survives because it lives in Orleans grain storage.
  - **Invoke timeout**: fixture agent sleeps 120s on one tool call with a 5s invoke timeout → `python-agent-invoke-timeout` URN; subprocess still Ready; next invoke succeeds.
  - **State too large**: fixture returns 2 MiB state → `python-agent-state-too-large`; previous state unchanged.
  - **Cross-silo activation**: spin two silos, invoke on silo A, activate the grain on silo B (deactivate + re-call) → state loaded from Orleans, Python subprocess on silo B gets the same state blob, agent continues where it left off.
- [ ] Full solution builds 0 warnings / 0 errors; CI green (Python tests skipped by default); opt-in integration run green locally.

### PR 3 — Docs + PublicAPI promotion + milestone + tag

- [ ] New concept doc `docs/concepts/polyglot-agents.md`:
  - Scope (agent-level, first-class; contrast with v0.23 tool-level and A2A external-level)
  - The three-way comparison table: A2A external / Python tool plugin / Python agent plugin
  - Wire protocol summary (JSON-RPC over stdio, `vais/agent.*` methods)
  - State model (opaque blob, Orleans-persisted, single-writer per session)
  - `plugin.yaml` schema — `kind: agent-handler` + `handler.typeName`
  - Lifecycle (subprocess spawned at startup, per-session state on demand, timeout ≠ restart)
  - What the Python author writes (just the `invoke` coroutine)
  - Not-in-scope: streaming, hot-reload, Python-side tool-call visibility
- [ ] New guide `docs/guides/package-a-python-agent.md`:
  - Scaffold with `uv init` + `uv add vais-agent-sdk langgraph`
  - Write the `invoke` function around a LangGraph graph
  - `plugin.yaml` + `pyproject.toml`
  - `uv venv --relocatable`
  - `Dockerfile.overlay`
  - Deploy: `vais plugin install <dir>` + `vais agent apply -f manifest.yaml`
  - Test: `vais invoke` / HTTP POST
  - Troubleshooting: handshake timeout, invoke timeout, state-too-large, handler collision
- [ ] `docs/reference/problem-details-urns.md` — five new URNs in a v0.24 section.
- [ ] `docs/reference/runtime-configuration.md` — Python agent sub-section under Plugin loader; note state-size cap env var (`VAIS_PYTHON_AGENT_MAX_STATE_BYTES`, default 1048576).
- [ ] `docs/roadmap/deferred-backlog.md` — §3 Plugins & hosting: mark **PARTIALLY SHIPPED v0.24** (Python tool *and* agent; Node/Go/Rust remain deferred).
- [ ] `PublicAPI.Shipped.txt` promotion for any public types landed in PR 1 (expect only DI extensions + `PythonPluginUrns` constants).
- [ ] Milestone entry appended to `plans/actor-agents-oss-milestone-log.md`.
- [ ] Tag `v0.24.0-preview`.

**Sizing:** PR 1 ≈ 3-4 days, PR 2 ≈ 3-4 days, PR 3 ≈ 1-2 days. **Total 7-10 working days** (~2 weeks).

---

## Wire protocol sketch

```
     .NET shim (PythonAgentShim)                Python subprocess (vais-agent-sdk)
     │                                          │
     │── vais/agent.invoke ─────────────────▶   │
     │   { agent_id, session_id,                │   state = request.state (may be null)
     │     user_message,                        │   new_state, reply = await invoke(state, user_message)
     │     state: <opaque json or null>,        │
     │     timeout_seconds: 60,                 │
     │     context: { trace_id, tenant_id } }   │
     │                                          │
     │   ◀───────────────────── response ───────│
     │   { assistant_message,                   │
     │     new_state: <opaque json>,            │
     │     usage: [{provider,in,out}...],       │
     │     journal: [{type,payload}...] }       │
     │                                          │
     │  → append ChatTurns to IAgentSession     │
     │  → persist new_state via IStateStore     │
     │  → forward journal to IJournalWriter     │
     │  → emit usage metrics                    │
     │                                          │
     │── vais/agent.reset ──────────────────▶   │
     │   { agent_id, session_id }               │   on_reset?(session_id)
     │   ◀── { } ───────────────────────────────│
     │                                          │
     │  → DeleteAsync state + Session.ResetAsync│
```

Methods reserved for future use (not implemented v0.24): `vais/agent.stream` (notifications-based streaming), `vais/agent.listSessions` (debug), `vais/agent.describe` (capability advertisement).

---

## Composition-root sketch

```csharp
public static void ConfigureServices(IServiceCollection services, RuntimeOptions options)
{
    // ... v0.17-v0.23 wiring unchanged ...

    if (!string.IsNullOrWhiteSpace(options.PythonPluginsDirectory))
    {
        services.AddPythonPlugins(new PythonPluginLoaderOptions
        {
            PluginsDirectory = options.PythonPluginsDirectory,
        });

        // v0.24 — Orleans-backed per-session state store for Python agents
        services.AddOrleansPythonAgentStateStore();
    }
}
```

Consumer-side (no new env vars beyond v0.23's `VAIS_PYTHON_PLUGINS_DIRECTORY`):

```bash
export VAIS_PYTHON_PLUGINS_DIRECTORY=/var/lib/vais/plugins
export VAIS_PYTHON_AGENT_MAX_STATE_BYTES=2097152   # optional override, default 1 MiB
```

---

## Plugin.yaml extension (additive, back-compat)

```yaml
apiVersion: vais/v1
kind: AgentPlugin
metadata:
  name: langgraph-researcher
spec:
  runtime: python
  kind: agent-handler                                       # NEW v0.24; absent = mcp-tool-server (v0.23)
  handler:                                                  # NEW v0.24
    typeName: langgraph_researcher.agent.ResearcherAgent    # matches AgentManifest.Handler.TypeName
  entrypoint: src/langgraph_researcher/server.py
  python:
    version: "3.13"
    interpreter: .venv/bin/python
  health:
    handshakeTimeoutSeconds: 5
    restartPolicy: exponentialBackoff
    invokeTimeoutSeconds: 60                                # NEW v0.24; default 60
```

---

## Risks + mitigations

- **State-blob wire cost per turn.** A LangGraph with 100 KiB of accumulated state round-trips that much over stdio per invoke. **Mitigation**: 1 MiB hard cap with clear URN; guide recommends trimming history server-side; v0.24.x can add delta-based state updates if profiling shows hot paths.
- **Single-writer-per-session is enforced by Orleans grain, not by Python.** Two concurrent invokes against the same session on different silos would race on the state store. **Mitigation**: `AiAgentGrain`'s Orleans activation model already guarantees single-writer per grain key; `(agentId, sessionId)` is the grain key. Python subprocess sees one invoke at a time per session. No change needed — reusing existing invariant.
- **Subprocess crash loses in-flight invocations.** If the subprocess dies mid-`vais/agent.invoke`, the call fails. **Mitigation**: restart resumes the subprocess; state was last persisted at previous invoke completion, so the failed call is "never happened" — client retries. Document the retry model.
- **Python-side SDK version skew.** Plugin built against vais-agent-sdk v0.24 running against a v0.25 runtime (or vice-versa). **Mitigation**: `VaisRuntimeAbi.CurrentVersion` check already covers the v0.23 case; extend the ABI match to include a `vais-agent-sdk` major version gate at handshake.
- **Streaming consumers get degraded responses silently.** v0.24 doesn't implement `IStreamingAiAgent`; consumers hitting `/v1/agents/{id}/invoke?stream=1` get a non-streaming response wrapped in a single-event stream. **Mitigation**: startup log at Information says `"Python agent 'X' does not support streaming; consumers will receive a single-event fallback."` Document v0.24.x streaming as a specific roadmap item.
- **Handler-collision with .NET plugin.** Python `handler.typeName` identical to a .NET `[VaisPlugin]` handler — which wins? **Mitigation**: refuse-both precedent from v0.23 decision 10. `python-agent-handler-collision` URN; neither loads; operator resolves.
- **Python plugin advertising an invalid state blob (non-JSON / binary).** Runtime can't parse. **Mitigation**: `python-agent-protocol-error` URN; state unchanged; caller sees a failed invoke with URN but session continues from last known good state.
- **CLI deploy story unverified for agent plugins.** `vais plugin install` was written for v0.23 tool plugins. Agent plugins use the same directory shape, but `plugin.yaml` has a new discriminator. **Mitigation**: PR 3 acceptance test exercises `vais plugin install` for both sample plugins end-to-end; any CLI changes needed get folded into PR 3.
- **LangGraph is not the only Python framework.** Plugin authors may want LangChain, CrewAI, AutoGen, or a custom loop. **Mitigation**: the SDK surface is framework-neutral (one `invoke` coroutine). The sample uses LangGraph because it's the most requested; the guide explicitly says any Python agent framework works.

---

## Acceptance

Pillar is done when:

- [ ] A `langgraph-researcher` sample plugin, packaged per the guide, loads at runtime startup with Status=Ready and registers its handler via `IPluginHandlerRegistry`.
- [ ] A declarative agent manifest with `Handler.TypeName: langgraph_researcher.agent.ResearcherAgent` gets translated to a `PythonAgentShim` by the existing translator plugin branch.
- [ ] `vais agent apply -f research-agent.yaml && vais invoke research-agent --input "..."` succeeds, returning the Python-generated response.
- [ ] State persists across turns: invoke twice against the same session; the second invoke sees the first invoke's `new_state`.
- [ ] State persists across subprocess restarts: force `SIGKILL` the subprocess between turns; next invoke loads state from Orleans and continues.
- [ ] State persists across silos: invoke on silo A, relocate the grain to silo B, next invoke on silo B continues from the same state.
- [ ] OPA policy engine, journal, audit, control-plane idempotency all work uniformly for Python agents (no Python-specific branches in consumer code).
- [ ] Invoke timeout returns `python-agent-invoke-timeout` URN without killing the subprocess; next invoke succeeds.
- [ ] State > 1 MiB returns `python-agent-state-too-large`; previous state intact.
- [ ] Handler collision between a Python agent and a .NET plugin refuses both (precedent from v0.23).
- [ ] Startup log reports each Python agent plugin: handler typeName registered, status, subprocess PID.
- [ ] Full solution 0 warnings / 0 errors; every test project green; Python-gated integration tests runnable via `VAIS_RUN_PYTHON_PLUGIN_TESTS=1`.
- [ ] Docs published; three-way comparison (external A2A / Python tool / Python agent) crystal-clear in `concepts/polyglot-agents.md`.
- [ ] Tag `v0.24.0-preview` created.

---

## Progress log

- 2026-04-24 — Pillar plan created. Written after v0.23 (Python tool plugins) tagged. Motivated by user observation that A2A doesn't make Python agents first-class in the "Kubernetes for agents" model — no Orleans durability, not CLI-deployable. Design extends v0.23's subprocess machinery to the agent level: one supervisor, multiplexed stdio (`vais/agent.*` methods alongside MCP's `tools/*`), Orleans-persisted opaque state blob per session, `PythonAgentShim : IAiAgent` handed into the existing `StatefulAgentOptions.Agent` seam. Three-PR sequence; ~7-10 working days. Streaming + hot-reload + delta-state deferred to v0.24.x.
