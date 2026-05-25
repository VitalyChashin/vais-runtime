# mcp-stdio-template

Template for publishing a stdio-only MCP server (anything from
[`modelcontextprotocol/servers`](https://github.com/modelcontextprotocol/servers), any
PyPI package exposing the MCP stdio protocol, any tool that speaks MCP over stdin/stdout)
as a runtime-supervised container against a Vais.Agents runtime — no runtime image rebuild,
no out-of-band sidecar, no third-party stdio↔HTTP bridge.

The bridge (`bridge.py`) wraps the stdio child with [FastMCP](https://github.com/jlowin/fastmcp)'s
`as_proxy()` and re-exposes it over streamableHttp at `/mcp`, with a `/health` endpoint the
runtime's `DockerContainerSupervisor` polls.

## Three-step recipe

1. Copy this directory next to your project's other resources.
2. Edit the `Dockerfile` — add a `RUN pip install <your-stdio-mcp-package>` line (or copy
   your binary into the image).
3. Edit `mcp-example.yaml` — set `metadata.id`, point `spec.container.build.context` at
   the directory holding your Dockerfile, and set `MCP_STDIO_CMD` to the command that
   starts your stdio MCP server. Then:

```bash
vais apply -f mcp-example.yaml
# → builds vais-mcp-<id>:<version>, registers, supervises the container.
```

The runtime opens an MCP client over streamableHttp to the bridge port. Agents that
reference this server via `mcpServers[].name + transport: registered` and
`tools[].source: mcp:<id>` see all tools the upstream stdio server exposes.

## Layout

```
mcp-stdio-template/
├── bridge.py          # FastMCP proxy + /health endpoint (do not edit)
├── Dockerfile         # add your stdio MCP install here
├── mcp-example.yaml   # McpServer manifest example
└── README.md          # this file
```

## Bridge env vars

The bridge reads its behavior from env vars. The most useful ones are settable from
`spec.container.env` in the manifest.

| Var | Default | Purpose |
|---|---|---|
| `MCP_STDIO_CMD` | (required) | Full command for the stdio MCP child, e.g. `python -m mcp_server_fetch`. |
| `MCP_BRIDGE_PORT` | `7000` | Port the bridge listens on. Must match `spec.container.port`. |
| `MCP_BRIDGE_PATH` | `/mcp` | URL path. Must match `spec.container.path`. |
| `MCP_HEALTH_PATH` | `/health` | Health endpoint path. Must match `spec.container.healthPath`. |
| `MCP_SERVER_NAME` | `vais-mcp-bridge` | Identity reported on MCP `initialize`. |

## Telemetry (opt-in)

When `Vais__ContainerPlugin__CallTokenSecret` is configured on the runtime, it
automatically injects the following env vars into every container MCP server container:

- `OTEL_EXPORTER_OTLP_ENDPOINT` / `OTEL_EXPORTER_OTLP_PROTOCOL` /
  `OTEL_EXPORTER_OTLP_HEADERS` — point the OpenTelemetry SDK at the runtime's OTLP
  receiver, authenticated with a `vais-plugin-token`.
- `OTEL_RESOURCE_ATTRIBUTES` — tags emitted spans with `vais.agent_id=<server-id>`.
- `VAIS_LOG_ENDPOINT` / `VAIS_LOG_TOKEN` — authenticated endpoint for `POST /v1/logs`
  structured-log records.

The stdio child process inherits all of these from the bridge process. To activate span
forwarding, install `opentelemetry-sdk opentelemetry-exporter-otlp-proto-http` (e.g. add
to your `Dockerfile`'s `pip install` line); the SDK picks up the env vars automatically.
Do not set these vars manually in `spec.container.env`.

## Concrete example

[`samples/mcp-fetch-container/`](../mcp-fetch-container/) wraps `mcp-server-fetch`
(PyPI) and is the canonical worked example.

## Why this exists

Most public MCP servers ship as stdio-only — the official `mcp/fetch` image, every
server in `modelcontextprotocol/servers`, every PyPI/uvx-distributed MCP server. The
runtime's MCP transport is `streamableHttp`, so users either had to vendor a third-party
stdio↔HTTP bridge (e.g. `supergateway`) into their compose stack or write a custom one.
This template lets the runtime own the supervision: same `vais apply -f` shape as every
other resource (P11), same hardening as container plugins (P12), same gateway middleware
applies to every tool call (P4).

Background: [`research/mcp-stdio-native-deployment-2026-05-17.md`](../../../research/mcp-stdio-native-deployment-2026-05-17.md).
Implementation plan: [`plans/mcp-stdio-native-impl-2026-05-17.md`](../../../plans/mcp-stdio-native-impl-2026-05-17.md).
