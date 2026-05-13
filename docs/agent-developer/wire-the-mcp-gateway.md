# Wire the MCP gateway

You'll register an MCP tool server, wrap it in an `McpGatewayConfig` middleware chain, and give your agent a tool that flows through it. Every tool call — like every LLM call — is observable, rate-limited, and policy-gated by the gateway. End state: your agent successfully fetches a web page via an MCP `fetch` tool, with logging, OTel, and rate-limit middleware applied transparently.

## Why this matters

Tools are the agent's hands. Without a gateway, every tool call is a one-off integration; with one, every call is logged, traced, retried under a circuit breaker, and gated by workspace policy — uniformly across every tool source.

## Prerequisites

- A running runtime ([DevOps section](../devops/index.md)).
- The CLI pointed at it.
- An agent registered with the runtime ([Your first declarative agent](your-first-declarative-agent.md)).
- A reachable MCP server. This tutorial uses the public `modelcontextprotocol/servers/fetch` image:

  ```bash
  docker run -d --name mcp-fetch -p 3000:3000 ghcr.io/modelcontextprotocol/servers/fetch:latest
  curl -s http://localhost:3000/health   # → ok
  ```

## Step 1 — Declare the gateway config

Save as `observable-mcp-gateway.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: McpGatewayConfig
metadata:
  id: observable-mcp-gateway
  version: "1.0"
  description: Logging + OTel + 30/min rate limit + response truncation.
spec:
  middleware:
    - name: ToolLogging
    - name: ToolOtel
    - name: ToolRateLimit
      params:
        callsPerMinute: 30
    - name: ToolResponseTruncation
      params:
        maxCharacters: 8192
```

**What these do:**

- `ToolLogging` — debug-level dispatch trace per tool call (tool name, call id, success/error).
- `ToolOtel` — `tool.gateway/{toolName}` OTel span per call. Tagged with `vais.tool.name`, `vais.tool.call_id`, `vais.workspace.id`. Sets `ActivityStatusCode.Error` and `vais.error.type` on failure.
- `ToolRateLimit` — sliding-window cap. Per-workspace, per-tool. Returns `Error = "ToolRateLimitExceeded"` when exhausted; the model observes the error and can adapt.
- `ToolResponseTruncation` — caps tool result size before the model sees it. Appends `[Truncated: response exceeded N characters]` so the model knows it was cut. Error outcomes are never truncated.

Order: outermost first. Policy, deny filters, and circuit breakers go outermost; logging and OTel go innermost (so they observe the actual dispatch, not short-circuits).

Apply:

```bash
vais apply -f observable-mcp-gateway.yaml
```

## Step 2 — Register the MCP server

Save as `mcp-fetch-server.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: McpServer
metadata:
  id: mcp-fetch
  version: "1.0"
  description: HTTP fetch tool via MCP.
spec:
  transport: streamableHttp
  url: http://host.docker.internal:3000/mcp
  mcpGatewayRef: observable-mcp-gateway
```

On Docker Desktop, `host.docker.internal` resolves to the host from inside the runtime container. On Linux without Docker Desktop, use the container's host-network address or join the runtime + MCP server to a shared user-defined bridge.

```bash
vais apply -f mcp-fetch-server.yaml
```

## Step 3 — Give your agent the tool

Edit your agent manifest:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: researcher
  version: "1.0"
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  systemPrompt:
    inline: |
      For each user question, call the `fetch` tool with a relevant URL and summarise
      what you found in 2-3 sentences. Cite the URL.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  mcpGatewayRef: observable-mcp-gateway      # ← agent's tool calls flow through this
  mcpServers:
    - name: mcp-fetch
      transport: registered                  # ← reference the registered server above
  tools:
    - name: fetch
      source: mcp:mcp-fetch                  # ← from the mcp-fetch server
  budget:
    maxTurns: 5
```

```bash
vais apply -f researcher.yaml
```

## Step 4 — Invoke and observe

```bash
vais invoke researcher --text "What's on example.com?"
docker logs -f vais-runtime
```

In the runtime logs you'll see, in order:

1. The model decides to call `fetch` with an argument.
2. `ToolLogging` records the dispatch.
3. `ToolOtel` opens the `tool.gateway/fetch` span.
4. `ToolRateLimit` records the call against the sliding window.
5. The MCP server returns content.
6. `ToolResponseTruncation` caps it at 8192 chars (if needed) before the model sees it.
7. Model summarises and returns the answer.

## Step 5 — Trip the rate limit

```bash
for i in {1..40}; do
  vais invoke researcher --text "Fetch https://example.com — request $i"
done
```

Around the 30th call you'll see `ToolRateLimitExceeded` outcomes flowing back to the model. The model observes the error string — no exception bubbles up to the agent caller; the conversation continues with the model adapting.

## What you built

- A reusable `McpGatewayConfig` covering logging, tracing, rate limiting, and result truncation.
- An `McpServer` registration that any agent can reference by `name`.
- A `researcher` agent that calls `fetch` through the gateway — every call observable, rate-limited, and capped, with zero per-tool code.

## Next

- **[Ship a Python agent](ship-a-python-agent.md)** — when YAML isn't expressive enough.
- [Full MCP middleware catalog](../guides/gate-tool-calls-with-the-tool-gateway.md) — circuit breaker, cache, argument validation, JSON repair, HTML→Markdown, workspace policy, output-length guard.
- [Concepts → Tools](../concepts/tools.md) — `ITool`, `IToolRegistry`, `IToolSource`, schema generation.
- [Concepts → Gateway config control plane](../concepts/gateway-config-control-plane.md) — how gateway configs and MCP servers are stored, versioned, and resolved at invoke time.
