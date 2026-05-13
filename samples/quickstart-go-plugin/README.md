# quickstart-go-plugin

Minimal **container plugin in Go** — companion sample for the [Author a container plugin in Go](../../docs/deep-development/author-a-container-plugin-in-go.md) tutorial. The smallest legal IP-1 plugin: three HTTP endpoints, standard library only, ~85 lines total.

**Demonstrates:** the IP-1 HTTP protocol is the entire plugin contract. No Go SDK ships — the protocol is small enough that any language with HTTP + JSON support can author a plugin directly.

**Needs API key:** no — the agent is a deterministic echo, not an LLM call.

## Layout

```
.
├── plugin.yaml       # runtime descriptor (kind: ContainerPlugin)
├── Dockerfile        # multi-stage build; ~10 MB static binary on scratch
├── go.mod
├── main.go           # three HTTP handlers
└── README.md
```

The handler routing key is `examples.echo.EchoPlugin` — `plugin.yaml`'s `/v1/metadata` response and the agent manifest's `spec.handler.typeName` must both match this string.

## Endpoints

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/health` | Liveness. Returns 200 + `"ok"`. |
| `GET` | `/v1/metadata` | Returns `{handlerTypeName, apiVersion}`. |
| `POST` | `/v1/invoke` | Echoes the last `user` message in `messages`. |

## Build and deploy

`vais apply -f plugin.yaml` reads `spec.build`, builds the image locally (only if the tag doesn't exist), and registers the plugin in one shot.

```bash
cd <repo>/agentic
vais apply -f samples/quickstart-go-plugin/plugin.yaml
# vais-quickstart-go-plugin:1.0 built ✓
# quickstart-go-plugin created (container-plugin, version 1.0)

vais plugin-status
# NAME                   KIND        TOPOLOGY    STATE   IMAGE
# quickstart-go-plugin   Container   standalone  Ready   vais-quickstart-go-plugin:1.0
```

Apply an agent that routes to the plugin:

```yaml
# echo-agent.yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: echo
  version: "1.0"
spec:
  handler:
    typeName: examples.echo.EchoPlugin
  protocols:
    - kind: Http
```

```bash
vais apply -f echo-agent.yaml
vais invoke echo --text "Hello!"
# echo: Hello!
```

## Iterate

Edit `main.go`, bump `spec.image` in `plugin.yaml` to `:1.1`, re-apply:

```bash
vais apply -f samples/quickstart-go-plugin/plugin.yaml
# → builds :1.1, PATCHes the runtime, drain-replace happens
```

The runtime drains in-flight `/v1/invoke` calls, replaces the container with the new image, waits for `/health`, and resumes.

## Going further

This sample doesn't call an LLM or tools — its job is to demonstrate the protocol. A production Go plugin would:

- Call `request.llmGatewayUrl` to route model calls through the runtime's middleware chain (rate limit, OTel, Langfuse, token budget).
- Call `request.toolGatewayUrl` to invoke MCP tools through the runtime's gateway.
- Round-trip state via `opaqueState`.
- Authenticate gateway calls using `request.context.callToken`.

Patterns mirror the Python reference at [`samples/PluginAgentLangGraphResearcherLive/`](../PluginAgentLangGraphResearcherLive/). The wire shapes are the same; only the HTTP client library changes.

## Related

- [Author a container plugin in Go](../../docs/deep-development/author-a-container-plugin-in-go.md) — the walkthrough.
- [Concepts → Polyglot plugins](../../docs/concepts/polyglot-plugins.md) — IP-1 protocol design.
- [`samples/quickstart-python-planner/`](../quickstart-python-planner/) — Python equivalent.
