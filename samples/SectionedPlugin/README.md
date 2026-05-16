# Sample: SectionedPlugin

Reference container plugin that opts into the **typed `Section[]` view** of per-turn context (Phase 4 — SC-20–SC-24). Demonstrates the end-to-end flow shipped with the section pipeline:

1. The runtime composes per-turn sections (persona, policy, retrieval, history) as it would for a declarative agent.
2. The plugin calls **`POST /v1/container-gateway/sections/build`** to fetch the resolver-ordered `Section[]` instead of consuming the pre-flattened `InvokeRequest.messages`.
3. The plugin logs a per-section breakdown for operator visibility, then flattens via `sections_to_openai_request()`.
4. The flattened body is POSTed back through the runtime's gateway-proxied `/v1/container-gateway/chat/completions` so token accounting + Langfuse + the LLM gateway middleware chain all apply normally.

This is the **opt-in** path. The default plugin behaviour (consume `InvokeRequest.messages`) keeps working unchanged — anyone who doesn't want per-section attribution or RAG visibility can ignore this sample entirely.

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

## What the plugin does, in 6 lines

```python
sections = await build_sections(
    gateway_base_url=req.llm_gateway_url, call_token=req.call_token,
    run_id=req.run_id, agent_id=req.agent_id,
    messages=[{"role": "user", "content": req.user_message}],
)
body = sections_to_openai_request(sections)
body["model"] = "gpt-4o-mini"
# ... post body to /v1/container-gateway/chat/completions ...
```

The runtime is the source of truth for what producers ship sections; the plugin owns conversation state and decides which sections to use. The default OpenAI adapter reproduces the pre-flattened `InvokeRequest.messages` shape exactly — so the *minimum* opt-in cost is one extra HTTP round-trip and gives back per-producer attribution + the ability to swap in a custom adapter (LangGraph state slots, LangChain `ChatPromptTemplate` parts, SGR planner inputs — see follow-on SC-25 issues).

## See also

- [Contract: gateway-internal.md](../../contracts/plugin-container/gateway-internal.md) — endpoint contract v0.26.
- [Wire context sections guide](../../docs/guides/wire-context-sections.md) — the runtime-side authoring tutorial.
- [`vais_agent_sdk.sections`](../python-agent-sdk/src/vais_agent_sdk/sections.py) — the Python helper.
- [`vais_agent_sdk.adapters.openai`](../python-agent-sdk/src/vais_agent_sdk/adapters/openai.py) — the reference adapter.
