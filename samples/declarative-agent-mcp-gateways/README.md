# declarative-agent-mcp-gateways

Deploy a research agent that uses declarative LLM and MCP gateway pipelines. Zero C# — every
governance, observability, and reliability behaviour is configured in YAML manifests.

Two variants are included:

| Variant | Agent manifest | How tools arrive |
|---|---|---|
| **Explicit** (original) | `research-agent.yaml` | Each tool listed in `tools[]` |
| **Virtual server** | `virtual-agent.yaml` | One `mcpServers[]` line; `tools[]` not needed |

The virtual-server variant demonstrates the IBM Context Forge-compatible consumption model: curate
the toolset once in `virtual-fetch.yaml`, then bind it with a single line in every agent that needs it.

**Concepts:** [gateway config control plane](../../docs/concepts/gateway-config-control-plane.md),
[declarative agents](../../docs/concepts/declarative-agents.md).
**Needs API key:** yes — `OPENAI_API_KEY` (agent uses `gpt-4o-mini`).
**Code:** 0 lines — YAML manifests only.

---

## Architecture

```
   invoke                 LLM gateway pipeline          Tool gateway pipeline
   ──────►  research-agent ──┬── LlmLogging  ──►  gpt-4o-mini
                             ├── LlmOtel
                             └── Prometheus

                          fetch tool call ──┬── ToolOtel  ──►  mcp-fetch
                                            ├── ToolRateLimit (30 rpm)
                                            └── ToolResponseTruncation (8192 chars)
```

`LlmGatewayConfig` and `McpGatewayConfig` are independent named pipelines. An agent binds to
both by ref. Changing a gateway config (re-`apply`) updates all agents bound to it on the next
activation — no redeployment needed.

---

## Prerequisites

- Docker + Docker Compose
- `vais` CLI on PATH (see [install](../../docs/devops/install-the-cli.md))
- `OPENAI_API_KEY` environment variable set

---

## Quickstart

### 1. Start the runtime and the MCP fetch sidecar

```bash
cd samples/declarative-agent-mcp-gateways
docker compose up -d
```

The compose file starts two containers:

| Container | Role |
|---|---|
| `vais-runtime` | Vais.Agents runtime (localhost mode) |
| `mcp-fetch` | MCP fetch server (streamableHttp on port 3000) |

Verify health:

```bash
curl http://localhost:8080/healthz   # → ok
```

Set the CLI context:

```bash
vais config set-context local --server http://localhost:8080
vais config use-context local
```

### 2. Apply manifests in dependency order

Gateway configs and the registered MCP server must be applied **before** the agent, because
`POST /v1/agents` eagerly validates `llmGatewayRef` and `mcpGatewayRef` against the registries.

**Explicit-tools variant (original):**

```bash
vais apply -f llm-gateway.yaml
# applied LlmGatewayConfig demo-llm-gateway@1.0

vais apply -f mcp-gateway.yaml
# applied McpGatewayConfig demo-mcp-governance@1.0

vais apply -f fetch-server.yaml
# applied McpServer mcp-fetch@1.0

vais apply -f research-agent.yaml
# applied Agent research-agent@1.0
```

**Virtual-server variant** — adds a virtual server that curates the backend, and an agent that
consumes it with a single `mcpServers[]` line and no `tools[]`:

```bash
vais apply -f llm-gateway.yaml
vais apply -f mcp-gateway.yaml
vais apply -f fetch-server.yaml       # physical upstream

vais apply -f virtual-fetch.yaml      # virtual server curates fetch → web_fetch
# applied McpServer virtual-fetch@1.0

vais apply -f virtual-agent.yaml      # binds the virtual server; no tools[] needed
# applied Agent virtual-agent@1.0
```

### 3. Invoke

```bash
# Explicit variant
vais invoke research-agent --text "What is Model Context Protocol?"

# Virtual-server variant
vais invoke virtual-agent --text "What is Model Context Protocol?"
```

Stream token-by-token:

```bash
vais invoke research-agent --text "Summarise the Vais.Agents README." --stream
```

### 4. Inspect the registered topology

```bash
vais get llm-gateways
# NAME                VERSION
# demo-llm-gateway    1.0

vais get mcp-gateways
# NAME                  VERSION
# demo-mcp-governance   1.0

vais get mcp-servers
# NAME             VERSION  VIRTUAL
# mcp-fetch        1.0      false
# virtual-fetch    1.0      true    ← only when virtual-fetch.yaml was applied

vais get agents
# NAME              VERSION  STATUS
# research-agent    1.0      Active
# virtual-agent     1.0      Active  ← only when virtual-agent.yaml was applied
```

---

## Manifests explained

### `llm-gateway.yaml` — LlmGatewayConfig

Three ordered middleware layers applied to every LLM call made by any agent bound to this config:

| Layer | What it does |
|---|---|
| `LlmLogging` | Structured log entry per request/response (provider, model, token counts) |
| `LlmOtel` | OpenTelemetry span wrapping the provider call |
| `Prometheus` | Increments `vais_llm_requests_total`, `vais_llm_tokens_total` counters |

The optional `rateLimit.requestsPerMinute: 60` cap is metadata stored with the config; the
runtime does not enforce it automatically — wire a `ToolRateLimit`-equivalent middleware or your
own LLM throttle middleware to activate it.

### `mcp-gateway.yaml` — McpGatewayConfig

Three ordered middleware layers applied to every tool call dispatched through any agent bound to
this config:

| Layer | What it does |
|---|---|
| `ToolOtel` | OTel span wrapping the MCP tool call |
| `ToolRateLimit` | Sliding-window rate limiter (30 calls/minute, in-memory store) |
| `ToolResponseTruncation` | Truncates tool output to 8 192 characters to keep context windows bounded |

### `fetch-server.yaml` — McpServer

Registers the `mcp-fetch` container as a known MCP server. Setting `mcpGatewayRef` here applies
`demo-mcp-governance` to tool calls that reach this server when an agent has **no** agent-level
`mcpGatewayRef`. Because `research-agent` sets its own `mcpGatewayRef`, the agent-level gateway
wins (one active tool pipeline per agent at any time).

This is the **Option D** server-level inheritance mechanism: a server declares its governance
pipeline once; any agent that binds this server without its own `mcpGatewayRef` inherits it
automatically. Agent-level ref always takes precedence over server-level.

### `research-agent.yaml` — Agent (explicit-tools variant)

Binds to both gateway configs. Lists each tool explicitly in `tools[]` — the pre-existing pattern:

```yaml
llmGatewayRef: demo-llm-gateway     # per-agent LLM pipeline
mcpGatewayRef: demo-mcp-governance  # per-agent tool pipeline
mcpServers:
  - name: mcp-fetch
    transport: registered            # look up server in IMcpServerRegistry
tools:
  - name: fetch
    source: mcp:mcp-fetch            # explicit: only this tool imported from mcp-fetch
```

`transport: registered` tells the translator to expand the server ref from `IMcpServerRegistry`
at grain activation time rather than connecting directly from the manifest URL.

### `virtual-fetch.yaml` — McpServer (virtual)

A virtual `McpServer` that aggregates `mcp-fetch` and curates its toolset via `toolProjection`.
It also declares `mcpGatewayRef: demo-mcp-governance` — demonstrating server-level governance
inheritance (Option D):

```yaml
spec:
  virtual: true
  sources:
    - ref: mcp-fetch             # upstream physical server
  toolProjection:
    - name: web_fetch            # published name the model sees
      from: mcp-fetch
      sourceToolName: fetch      # source name on mcp-fetch
```

The projection renames `fetch` → `web_fetch`. Consumers of this virtual server see `web_fetch` only,
regardless of how many tools `mcp-fetch` exposes. This is the curation boundary.

### `virtual-agent.yaml` — Agent (virtual-server variant)

Binds only the virtual server. No `tools[]` entry needed — the runtime imports the projected toolset:

```yaml
mcpServers:
  - name: virtual-fetch
    transport: registered        # binding imports the virtual server's projected toolset
# No tools[] — web_fetch arrives automatically from virtual-fetch's toolProjection
```

This is the IBM Context Forge-compatible consumption model: curate once in the virtual server,
consume everywhere with a single line.

`virtual-agent` carries no `mcpGatewayRef` by design: governance is inherited from the bound
virtual server (`virtual-fetch`), which declares `mcpGatewayRef: demo-mcp-governance`. This
demonstrates Option D — the server-level gateway ref inheritance introduced alongside the
virtual-server binding model.

---

## Environment variables

| Variable | Required | Description |
|---|---|---|
| `OPENAI_API_KEY` | yes | Forwarded to the runtime container |
| `VAIS_OTEL_ENDPOINT` | no | OTLP collector for LlmOtel / ToolOtel spans |
| `VAIS_OTEL_CONSOLE` | no | Set `true` to print spans to stdout instead |

---

## Validation steps

The steps below confirm the full gateway-config control plane round-trip: apply → activate → invoke.

```bash
# 1. Start runtime + mcp-fetch sidecar
cd samples/declarative-agent-mcp-gateways
docker compose up -d

# 2. Deploy in dependency order
vais apply -f llm-gateway.yaml           # → applied LlmGatewayConfig demo-llm-gateway@1.0
vais apply -f mcp-gateway.yaml           # → applied McpGatewayConfig demo-mcp-governance@1.0
vais apply -f fetch-server.yaml          # → applied McpServer mcp-fetch@1.0
vais apply -f research-agent.yaml        # → applied Agent research-agent@1.0

# 3. Verify refs: wrong order → 422
#    Create a bad agent manifest that references a non-existent gateway:
cat <<'EOF' > /tmp/research-agent-bad.yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: research-agent-bad
  version: "1.0"
spec:
  model:
    provider: openai
    name: gpt-4o-mini
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  llmGatewayRef: does-not-exist
  tools: []
EOF
vais apply -f /tmp/research-agent-bad.yaml
# Expected: error 422 — llmGatewayRef 'does-not-exist' is not registered

# 4. Invoke and confirm a streamed response
vais invoke research-agent --text "What is Vais.Agents?" --stream
# Expected: token stream with fetched content

# 5. Inspect topology
vais get llm-gateways
vais get mcp-gateways
vais get mcp-servers
vais get agents

# 6. Update the rate limit without redeploying the agent
#    Edit mcp-gateway.yaml: set callsPerMinute: 60, then:
vais apply -f mcp-gateway.yaml
# Expected: applied McpGatewayConfig demo-mcp-governance@1.0
# The change takes effect on the next agent activation (cache is evicted).

# 7. Clean up
vais delete agents/research-agent
vais delete mcp-servers/mcp-fetch
vais delete mcp-gateways/demo-mcp-governance
vais delete llm-gateways/demo-llm-gateway
docker compose down
```

**Expected outcomes:**

- Step 2: all four `apply` commands succeed with `applied <kind> <id>@<version>`.
- Step 3: `vais apply` for the bad agent returns HTTP 422 with a message naming the missing ref.
- Step 4: streamed response arrives within the `maxTurns: 5` budget.
- Step 6: re-applying the gateway config does not require touching the agent manifest.
- Step 7: all `delete` commands return 204; `docker compose down` stops both containers cleanly.

---

## Clean up

```bash
vais delete agents/research-agent
vais delete mcp-servers/mcp-fetch
vais delete mcp-gateways/demo-mcp-governance
vais delete llm-gateways/demo-llm-gateway
docker compose down
```
