# Sample: SectionedPluginLegacy

Plugin-side-flatten variant of [SectionedPlugin](../SectionedPlugin/) — kept as a deliberate side-by-side comparison so plugin developers can see the trade-off between the two paths and pick the right one.

Both samples opt into the typed `Section[]` view via `POST /v1/container-gateway/sections/build`. They differ only in **where the flatten happens**:

| | SectionedPlugin (canonical, v0.27) | SectionedPluginLegacy (this sample) |
|---|---|---|
| Step 5 — flatten | runtime, via `CompletionRequestFlattener` | plugin, via `vais_agent_sdk.adapters.openai.sections_to_openai_request()` |
| Step 6 — LLM call | `POST /v1/container-gateway/llm/complete` with `{ sections: [...] }` body | `POST /v1/container-gateway/chat/completions` with `{ messages: [...], model, ... }` body |
| Wire shape consumed by client | VAIS-native | OpenAI Chat Completions |
| Per-section telemetry on the LLM-call span | ✅ fires (OTel section tags, Prometheus section histograms, Langfuse section aliases, `RequestSectionsBuilt` event) | ❌ silent — runtime sees a plain `CompletionRequest` |
| Gateway-level telemetry (token counts, model id, Langfuse trace, `GatewayEventMiddleware` row) | ✅ fires normally | ✅ fires normally |
| Integration with OpenAI-compatible SDKs / external IDE tools | requires the plugin to convert | works directly — the wire is OpenAI |

**Pick this sample's shape when:** the plugin needs the OpenAI chat-completions wire format because it's bridging a non-VAIS framework, driving an OpenAI-compatible SDK on the client side (so an external IDE / chat tool that expects OpenAI-shape SSE can consume the stream), or needs to talk to a hosted LLM directly through the gateway-proxied OpenAI surface.

**Pick the canonical sample's shape when:** the plugin just wants per-section observability and the standard gateway + middleware behaviour, with no constraint that forces the OpenAI wire shape. The canonical path is the default recommendation.

The legacy path is **not deprecated** — it's a permanent backwards-compatibility and escape-hatch contract. Both samples are supported indefinitely.

## Layout

```
samples/SectionedPluginLegacy/
├── README.md                          ← this file
├── sectioned-agent-legacy.yaml        ← agent manifest (identical shape to sectioned-agent.yaml)
└── sectioned-plugin-legacy/           ← container plugin sources
    ├── pyproject.toml
    ├── src/sectioned_plugin_legacy/
    │   ├── __init__.py                ← docstring with the side-by-side comparison
    │   ├── agent.py                   ← invoke() — the plugin-side-flatten flow
    │   └── server.py                  ← stdio entrypoint (vais_agent_sdk.run)
    └── tests/
        └── test_invoke.py             ← mocked-gateway integration test (asserts /chat/completions path)
```

## Running the integration test (no live runtime required)

```powershell
cd agentic/samples/SectionedPluginLegacy/sectioned-plugin-legacy
python -m pip install -e .
python -m pytest tests/ -v
```

Two scenarios:
- `test_invoke_routes_through_chat_completions_with_plugin_side_flatten` — drives one full turn against an `httpx.MockTransport`; asserts the two outbound calls (`/v1/sections/build` then `/v1/container-gateway/chat/completions`), auth headers, OpenAI-shape body (`messages` array, OpenAI `model` field), and the assistant reply + usage round-trip.
- `test_invoke_propagates_404_from_unknown_agent` — same error-propagation behaviour as the canonical sample's test.

The interesting reading is the diff against [`samples/SectionedPlugin/sectioned-plugin/tests/test_invoke.py`](../SectionedPlugin/sectioned-plugin/tests/test_invoke.py). The first call (`sections/build`) is identical in both; the second is where the paths diverge.

## What the plugin does, in 8 lines

```python
sections = await build_sections(
    gateway_base_url=req.llm_gateway_url, call_token=req.call_token,
    run_id=req.run_id, agent_id=req.agent_id,
    messages=[{"role": "user", "content": req.user_message}],
)
# (optional) inspect / mutate sections here

body = sections_to_openai_request(sections)   # ← plugin-side flatten
body["model"] = "gpt-4o-mini"
# POST body to /v1/container-gateway/chat/completions ...
```

Compare to the canonical sample's last two lines:

```python
result = await complete_from_sections(..., sections=sections, model_id="gpt-4o-mini")
# runtime flattens server-side; SectionTelemetryEmitter fires; LLM gateway runs
```

The behavioural difference is the telemetry trade-off. On this path the runtime's `SectionTelemetryEmitter` doesn't fire on the LLM-call span — the plugin's `/v1/sections/build` call gets its own per-section snapshot from that call's emitter, but downstream the runtime sees a `CompletionRequest` with no section info. Whether that matters depends on your operational story:

- If you watch the Grafana "Context Sections" dashboard or filter Langfuse traces by per-section ratio, you'll see this agent's LLM calls **missing** from those views (gateway-level telemetry still works, so they show up as ordinary completions; just without the section-level breakdown). The canonical path puts them back.
- If your observability story is OpenAI-compatible (the chat-completions span carries the prompt + completion tokens; you don't need per-section attribution), this path is fully fine — and you get the OpenAI wire shape for free.

## Running live against the local-dev runtime

Same shape as the canonical sample. Build + start the stack, apply the manifest, invoke:

```powershell
cd local-dev
.\dev.ps1 start -Metrics

cd ../agentic
dotnet run --project src/Vais.Agents.Cli -- apply -f samples/SectionedPluginLegacy/sectioned-agent-legacy.yaml `
    --endpoint http://localhost:8080
dotnet run --project src/Vais.Agents.Cli -- invoke sectioned-agent-legacy `
    --message "What's our return policy?" `
    --endpoint http://localhost:8080
```

You'll see the same plugin-side `section id=... kind=... producer=... chars=... order=... priority=...` log lines as the canonical sample. Compare the Langfuse generation panel for an `sectioned-agent` call vs an `sectioned-agent-legacy` call — both share the gateway-level metadata; only the canonical one carries the per-section `langfuse.section.*` aliases.

## See also

- [SectionedPlugin](../SectionedPlugin/README.md) — the canonical v0.27 sample. Read first if you're deciding which shape to adopt.
- [Wire context sections guide](../../docs/guides/wire-context-sections.md) — the runtime-side authoring tutorial; covers the observability surface in detail.
- [Contract: gateway-internal.md](../../contracts/plugin-container/gateway-internal.md) v0.27 — the protocol both samples conform to.
- [`vais_agent_sdk.adapters.openai`](../python-agent-sdk/src/vais_agent_sdk/adapters/openai.py) — the plugin-side flatten helper this sample uses.
