# Sample: SectionedPlugin

Reference container plugin that opts into the **typed `Section[]` view** of per-turn context (Phase 4 — SC-20–SC-24, contract v0.27). Demonstrates the canonical end-to-end flow:

1. The runtime composes per-turn sections (persona, policy, retrieval, history) as it would for a declarative agent.
2. The plugin calls **`POST /v1/container-gateway/sections/build`** to fetch the resolver-ordered `Section[]` instead of consuming the pre-flattened `InvokeRequest.messages`.
3. The plugin logs a per-section breakdown for operator visibility (and could mutate the section list here — drop noisy producers, override budgets, suppress metadata).
4. The plugin ships the section list **back** via **`POST /v1/container-gateway/llm/complete`** with the `{ sections: [...] }` body. The runtime runs the canonical pipeline server-side: `resolver → packer → telemetry emitter → flattener → LLM gateway`. Per-section OTel tags, Prometheus metrics, Langfuse enrichment, and the `RequestSectionsBuilt` event all fire — exactly the same as for a runtime-hosted agent.

This is the **opt-in** path. The default plugin behaviour (consume `InvokeRequest.messages`) keeps working unchanged. Plugins that integrate with non-VAIS toolchains (OpenAI-compatible SDKs, frameworks that drive the LLM call themselves) can use the legacy plugin-side flatten path — see `_invoke_legacy_path` in `agent.py` for the contrast. The legacy path remains supported but doesn't get the per-section telemetry symmetry the canonical path provides.

## Layout

```
samples/SectionedPlugin/
├── README.md                   ← this file
├── sectioned-agent.yaml        ← agent manifest (composer + handler binding)
└── sectioned-plugin/           ← container plugin sources
    ├── pyproject.toml
    ├── src/sectioned_plugin/
    │   ├── __init__.py
    │   ├── agent.py            ← invoke() — the end-to-end flow
    │   └── server.py           ← stdio entrypoint (vais_agent_sdk.run)
    └── tests/
        └── test_invoke.py      ← mocked-gateway integration test
```

## Running the integration test (no live runtime required)

```powershell
cd agentic/samples/SectionedPlugin/sectioned-plugin
python -m pip install -e .
python -m pytest tests/ -v
```

Two scenarios:
- `test_invoke_end_to_end_routes_through_sections_build_and_chat_completions` — drives one full turn through an `httpx.MockTransport` that stands in for the runtime gateway; asserts the two outbound calls, auth headers, body shapes, and the assistant reply + usage round-trip.
- `test_invoke_propagates_404_from_unknown_agent` — verifies that a 404 from `/v1/sections/build` raises `httpx.HTTPStatusError` so the plugin author can fall back to the default behaviour or alert.

## Running live against the local-dev runtime

The plugin runs as a container plugin under the standard local-dev path. From the repo root:

```powershell
# 1. Build + start the runtime stack (Prometheus / Grafana overlay optional).
cd local-dev
.\dev.ps1 start -Metrics

# 2. Apply the sectioned-agent manifest.
cd ../agentic
dotnet run --project src/Vais.Agents.Cli -- apply -f samples/SectionedPlugin/sectioned-agent.yaml `
    --endpoint http://localhost:8080

# 3. Invoke the agent.
dotnet run --project src/Vais.Agents.Cli -- invoke sectioned-agent `
    --message "What's our return policy?" `
    --endpoint http://localhost:8080
```

In the runtime logs you'll see the plugin emit a per-section breakdown line per turn (`section id=system.base kind=SystemSegment ...`). With this manifest the runtime emits a single `system.base` section from `systemPrompt.inline`; to see per-contributor sections (one per registered `ISystemPromptContributor`) wire `AggregatingSystemPromptComposer` into your runtime host's DI — see the [wire-context-sections guide](../../docs/guides/wire-context-sections.md) for the full setup. The test in `sectioned-plugin/tests/test_invoke.py` exercises the multi-section happy path via a fixture that simulates what the runtime would emit once you have contributors wired.

In Langfuse (`http://localhost:3000`) the per-turn span carries `langfuse.section.*` metadata aliases — same guide covers the observability surface end-to-end.

## What the plugin does, in 8 lines

```python
sections = await build_sections(
    gateway_base_url=req.llm_gateway_url, call_token=req.call_token,
    run_id=req.run_id, agent_id=req.agent_id,
    messages=[{"role": "user", "content": req.user_message}],
)
# (optional) inspect / mutate sections here

result = await complete_from_sections(
    gateway_base_url=req.llm_gateway_url, call_token=req.call_token,
    run_id=req.run_id, agent_id=req.agent_id,
    sections=sections, model_id="gpt-4o-mini",
)
return AgentResponse(assistantMessage=result.message["content"])
```

The runtime is the source of truth for what producers ship sections **and** for how those sections flatten into the wire request. The plugin owns conversation state and decides which sections to keep. This split is what gives the plugin per-section telemetry symmetry with runtime-hosted agents — the flatten + LLM call run server-side, so all the runtime's observability sinks see the section breakdown.

### Legacy path (kept for backwards compatibility — see the dedicated companion sample)

The pre-v0.27 shape — plugin flattens client-side via `sections_to_openai_request()` and POSTs to `/v1/container-gateway/chat/completions` — is preserved verbatim. It's the right choice when:

- You're driving an OpenAI-compatible SDK on the client side that expects the OpenAI chat-completions wire shape.
- You're integrating with a non-VAIS framework that doesn't fit the canonical path.
- You want to bypass the gateway for a specific call (talk to a hosted LLM directly).

Trade-off: per-section telemetry doesn't fire on the LLM-call span on that path — the runtime sees a `CompletionRequest` with no section info, so OTel section tags, Prometheus section metrics, Langfuse section enrichment, and the `RequestSectionsBuilt` event all stay silent for that call. Gateway-level telemetry (token counts, model id, Langfuse trace metadata) still fires normally; only the per-section attribution is lost.

A dedicated side-by-side sample — [`samples/SectionedPluginLegacy/`](../SectionedPluginLegacy/) — implements the plugin-side-flatten path end-to-end so you can diff the two against each other. Use that when deciding which shape fits your plugin.

Custom framework adapters (LangGraph state slots, LangChain `ChatPromptTemplate` parts, SGR planner inputs) are tracked as follow-on issues — see `epic:sectioned-context`.

## See also

- [Contract: gateway-internal.md](../../contracts/plugin-container/gateway-internal.md) — endpoint contract v0.26.
- [Wire context sections guide](../../docs/guides/wire-context-sections.md) — the runtime-side authoring tutorial.
- [`vais_agent_sdk.sections`](../python-agent-sdk/src/vais_agent_sdk/sections.py) — the Python helper.
- [`vais_agent_sdk.adapters.openai`](../python-agent-sdk/src/vais_agent_sdk/adapters/openai.py) — the reference adapter.
