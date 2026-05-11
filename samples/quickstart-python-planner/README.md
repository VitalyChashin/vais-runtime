# quickstart-python-planner

Minimal **container plugin** used by [QUICKSTART.md](../../QUICKSTART.md) step 6. Reimplements
the declarative `planner` agent as a separate Docker container that speaks the IP-1 HTTP
protocol. Demonstrates the container plugin model — single image, no runtime image rebuild,
hot-replaceable via `vais plugin push`.

**Concepts:** [polyglot agents](../../docs/concepts/polyglot-agents.md),
[container plugin model research](../../../research/plugin-container-model-2026-05-08.md).
**Needs API key:** yes — `OPENAI_API_KEY` (the plugin calls `gpt-4o-mini` directly via the OpenAI SDK).

## Layout

```
.
├── plugin.yaml       # runtime descriptor (runtime: container, image: ...)
├── Dockerfile        # builds the plugin's HTTP server image
├── pyproject.toml    # one dep: openai
└── server.py         # stdlib HTTP server implementing /health, /v1/metadata, /v1/invoke
```

The handler routing key is `quickstart_planner.QuickstartPlanner` — `plugin.yaml`'s
implicit identity (from `/v1/metadata`) and the agent manifest's `spec.handler.typeName`
must both match this string.

## Build and deploy (the path QUICKSTART uses)

`vais apply -f` reads `spec.build`, builds the image locally (only if the tag doesn't
exist yet), and registers the plugin with the running runtime in one shot.

```bash
cd <repo>/agentic
vais apply -f samples/quickstart-python-planner/plugin.yaml
# vais-quickstart-planner:1.0 built ✓
# quickstart-python-planner created (container-plugin, version 1.0)

vais plugin-status   # → quickstart-python-planner  Container  standalone  Ready
```

## Iterate

Edit `server.py`, bump `spec.image` in `plugin.yaml` to `:1.1`, re-apply:

```bash
# Edit plugin.yaml: spec.image: vais-quickstart-planner:1.1
vais apply -f samples/quickstart-python-planner/plugin.yaml
# → builds :1.1 (new tag), PATCHes the runtime, drain-replace happens
```

For CI pipelines that build and push separately:

```bash
vais plugin-build --image vais-quickstart-planner:1.1 --push
vais apply -f samples/quickstart-python-planner/plugin.yaml --no-build
```

The runtime drains in-flight `/v1/invoke` calls for this plugin, replaces the container
with the new image, waits for `/health`, and resumes serving. The .NET silo and Orleans
grain state are untouched.

## Protocol

The runtime sends `POST /v1/invoke` with this shape:

```json
{
  "agentId": "planner",
  "sessionId": "abc123",
  "messages": [
    {"role": "system", "content": "..."},
    {"role": "user",   "content": "What is MCP?"}
  ],
  "llmGatewayUrl":  "http://runtime-internal/v1/llm",
  "toolGatewayUrl": "http://runtime-internal/v1/tools",
  "opaqueState": null,
  "timeoutSeconds": 60,
  "context": { "runId": "...", "traceparent": "...", "callToken": "..." }
}
```

The plugin must respond with `{ "assistantMessage": "...", "opaqueState": null | "..." }`
on 2xx, or a structured error body `{ "errorType", "errorMessage", "diagnosticTail" }` on
4xx/5xx. The runtime's `ContainerAgentShim` parses both shapes and propagates failures into
`GraphFailed` events per architectural principle P9.

This sample calls OpenAI directly. A production plugin would route its LLM calls through
`request.llmGatewayUrl` so the runtime's middleware chain (rate limiting, OTel tracing,
Langfuse, token-budget enforcement) applies. See the gateway-injection design in
[`research/plugin-container-model-2026-05-08.md`](../../../research/plugin-container-model-2026-05-08.md).
