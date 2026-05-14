# Wire the MCP gateway

You'll register an MCP tool server, wrap it in an `McpGatewayConfig` middleware chain, and give your agent a tool that flows through it. Every tool call — like every LLM call — is observable, rate-limited, and policy-gated by the gateway. End state: your agent successfully fetches a web page via an MCP `fetch` tool, with logging, OTel, and rate-limit middleware applied transparently.

## Why this matters

Tools are the agent's hands. Without a gateway, every tool call is a one-off integration; with one, every call is logged, traced, retried under a circuit breaker, and gated by workspace policy — uniformly across every tool source.

## Prerequisites

- A running runtime ([DevOps section](../devops/index.md)).
- The CLI pointed at it.
- An agent registered with the runtime ([Your first declarative agent](your-first-declarative-agent.md)).
- Docker available on the host running the runtime. Pull the MCP fetch image once:

  ```bash
  docker pull mcp/fetch:latest
  ```

  The runtime spawns `mcp/fetch` as a stdio subprocess on each connection — no separate container to manage.

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
  transport: stdio
  command: docker
  args:
    - run
    - --rm
    - -i
    - mcp/fetch:latest
  mcpGatewayRef: observable-mcp-gateway
```

The runtime spawns `docker run --rm -i mcp/fetch:latest` as a child process and speaks the MCP stdio protocol over its stdin/stdout. No port to expose, no network routing required.

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
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
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
      transport: registered                  # ← binding the server imports its full toolset
  budget:
    maxTurns: 5
```

```bash
vais apply -f researcher.yaml
```

**Binding a server imports its toolset.** When a `transport: registered` entry has no matching `tools[]` entry, the runtime is in **import-all mode**: every tool the server exposes is imported automatically. For `mcp-fetch` that means the `fetch` tool, without any per-tool declaration in the agent manifest.

### Mode selection (D1)

How the runtime decides which mode a server is in, per server:

| Condition | Mode | What resolves |
|---|---|---|
| No `tools[]` entry with `source: mcp:<serverName>` | **Import-all** | Every tool the server exposes |
| At least one `tools[]` entry with `source: mcp:<serverName>` | **Explicit** | Only the tools listed in `tools[]` |

Explicit mode is the pre-existing behavior and is unchanged — every existing manifest keeps working exactly as before.

### Narrowing the import

If you want only a subset of the server's tools, add a `tools` allowlist directly on the `mcpServers` entry (not in `tools[]`):

```yaml
mcpServers:
  - name: mcp-fetch
    transport: registered
    tools:
      - fetch         # import only this tool; the server may expose others
```

Any name in the allowlist that the server does not expose fails at apply time with `urn:vais-agents:mcp-tool-not-found`.

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

<details><summary>PowerShell</summary>

```powershell
1..40 | ForEach-Object {
  vais invoke researcher --text "Fetch https://example.com — request $_"
}
```

</details>

Around the 30th call you'll see `ToolRateLimitExceeded` outcomes flowing back to the model. The model observes the error string — no exception bubbles up to the agent caller; the conversation continues with the model adapting.

## What you built

- A reusable `McpGatewayConfig` covering logging, tracing, rate limiting, and result truncation.
- An `McpServer` registration that any agent can reference by `name`.
- A `researcher` agent that binds `mcp-fetch` in one line and automatically gets its full toolset — every call observable, rate-limited, and capped, with zero per-tool enumeration.

## Next

- **[Ship a Python agent](ship-a-python-agent.md)** — when YAML isn't expressive enough.
- [Full MCP middleware catalog](../guides/gate-tool-calls-with-the-tool-gateway.md) — circuit breaker, cache, argument validation, JSON repair, HTML→Markdown, workspace policy, output-length guard.
- [Concepts → Tools](../concepts/tools.md) — `ITool`, `IToolRegistry`, `IToolSource`, schema generation.
- [Concepts → Gateway config control plane](../concepts/gateway-config-control-plane.md) — how gateway configs and MCP servers are stored, versioned, and resolved at invoke time.
