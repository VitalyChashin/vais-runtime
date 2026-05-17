# mcp-fetch-container

Concrete reference for the container MCP server pattern: wraps the official
[`mcp-server-fetch`](https://pypi.org/project/mcp-server-fetch/) PyPI package
behind a streamableHttp bridge so the runtime can supervise it as a single
container and treat it like any other MCP server.

This is the worked example of [`samples/mcp-stdio-template/`](../mcp-stdio-template/).

## Apply

From this directory (or pass the full path; build context resolves to the
directory containing `mcp-fetch.yaml`):

```bash
vais apply -f mcp-fetch.yaml
# → vais-mcp-mcp-fetch:1.0 built ✓
# → mcp-fetch created (mcp-server, version 1.0)
```

The runtime builds the image (skipped if the tag already exists), registers the
manifest, supervises a hardened container per P12 (read-only rootfs, dropped
caps, no-new-privileges, memory/CPU/PID limits, attached to
`VAIS_DOCKER_PLUGIN_NETWORK`), and opens an MCP client over streamableHttp
at `http://<container>:7000/mcp`.

Up to ~30 seconds after apply, agents that reference `mcp:mcp-fetch` as a tool
source can call the `fetch` tool. Bind any `McpGatewayConfig` via
`spec.mcpGatewayRef` to apply rate limiting, OTel spans, response truncation,
etc. to every tool call.

## Use from an agent

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata: { id: researcher, version: "1.0" }
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      Call the fetch tool with a relevant URL, then summarise in 2-3 sentences.
  handler: { typeName: declarative }
  protocols: [{ kind: Http }]
  llmGatewayRef: demo-llm-gateway
  mcpGatewayRef: demo-mcp-governance
  mcpServers:
    - name: mcp-fetch
      transport: registered                # look up by id in the runtime registry
  tools:
    - name: fetch
      source: mcp:mcp-fetch                # mcp:<server-id>
  budget: { maxTurns: 5 }
```

## How this differs from the QUICKSTART's `quickstart-mcp-fetch`

[`samples/quickstart-mcp-fetch/`](../quickstart-mcp-fetch/) is a toy server that
runs as its own compose service (the user manages the container themselves) and
the runtime connects to it over streamableHttp. This sample shifts ownership: the
runtime supervises the container via `IContainerSupervisor`, the manifest is
`vais apply -f`-publishable, drain-replace + resource limits + isolation are all
provided by the same infrastructure container plugins use.

## What gets built

| Layer | Source |
|---|---|
| Image tag | `vais-mcp-mcp-fetch:1.0` (derived from `metadata.id` + `metadata.version` when `spec.container.image` is omitted) |
| Bridge | [`bridge.py`](bridge.py) — FastMCP `as_proxy` over `StdioTransport`, custom `/health` route. Identical to `samples/mcp-stdio-template/bridge.py`. |
| Stdio child | `python -m mcp_server_fetch` (set via `MCP_STDIO_CMD` env var) |

Plan: [`plans/mcp-stdio-native-impl-2026-05-17.md`](../../../plans/mcp-stdio-native-impl-2026-05-17.md).
