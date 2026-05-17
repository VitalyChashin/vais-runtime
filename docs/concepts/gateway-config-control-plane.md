# Gateway config control plane

**v0.24 Pillar.** Named, versioned middleware pipelines (`LlmGatewayConfig`, `McpGatewayConfig`) that are stored in the control plane and applied to agents declaratively — no C# required, no redeployment needed to change a pipeline.

## What problem it solves

Without this feature, every `StatefulAiAgent` owns its own middleware chain, configured at construction time. If you want to change the rate limit on every agent, you redeploy. If you want observability on all tool calls across a fleet of agents, you touch every manifest or every factory.

The gateway config control plane makes middleware pipelines **first-class resources**: apply once, bind many agents.

## YAML kinds

Three new `apiVersion: vais.agents/v1` resource kinds:

| Kind | Purpose |
|---|---|
| `LlmGatewayConfig` | Named LLM middleware pipeline (logging, OTel, Prometheus, semantic cache, …) |
| `McpGatewayConfig` | Named tool middleware pipeline (OTel, rate limit, truncation, retry, …) |
| `McpServer` | Registered MCP server endpoint; can carry its own `mcpGatewayRef` as a fallback |

### LlmGatewayConfig

```yaml
apiVersion: vais.agents/v1
kind: LlmGatewayConfig
metadata:
  id: prod-llm-governance
  version: "1.0"
spec:
  middleware:
    - name: LlmLogging
    - name: LlmOtel
    - name: Prometheus
    - name: SemanticCache
  rateLimit:
    requestsPerMinute: 120
```

### McpGatewayConfig

```yaml
apiVersion: vais.agents/v1
kind: McpGatewayConfig
metadata:
  id: prod-mcp-governance
  version: "1.0"
spec:
  middleware:
    - name: ToolOtel
    - name: ToolRateLimit
      params:
        callsPerMinute: 60
    - name: ToolResponseTruncation
      params:
        maxCharacters: 8192
    - name: ToolRetry
      params:
        maxAttempts: 3
```

### McpServer (registered transport)

```yaml
apiVersion: vais.agents/v1
kind: McpServer
metadata:
  id: my-fetch-server
  version: "1.0"
spec:
  transport: streamableHttp
  url: http://mcp-fetch:3000/mcp
  mcpGatewayRef: prod-mcp-governance   # fallback gateway when agent has none
```

Supported `transport` values for physical servers:

| Value | Owns | Use when |
|---|---|---|
| `streamableHttp` | external service | the MCP server already exposes HTTP at a URL |
| `sse` | external service | legacy SSE-only MCP server |
| `stdio` | the runtime process (in-process subprocess) | the server binary is on the runtime host's `$PATH` |
| `containerStdio` | a runtime-supervised container | publishing a stdio-only MCP server (PyPI, `mcp/fetch`, etc.) against a containerised runtime; the runtime builds the image, starts a hardened container, wraps stdio↔streamableHttp internally. See [Guide: deploy a stdio-only MCP server](../guides/deploy-a-stdio-mcp-server.md). |

Set `virtual: true` (with `sources` + `toolProjection`) for a logical aggregator over multiple registered servers. Middleware applies to every transport uniformly.

## Two orthogonal axes

The MCP surface is two independent resources with distinct responsibilities:

| Resource | Controls | Does NOT own |
|---|---|---|
| `McpServer` (incl. `virtual: true`) | **What tools** the agent receives | Policy, middleware, rate limits |
| `McpGatewayConfig` | **What policy/middleware** governs tool calls | Server catalog, connection details |

These are intentionally separate. One governance pipeline can span many server sets; one server set can be reused across pipelines. Do **not** confuse `McpGatewayConfig` with an IBM Context Forge-style federation gateway — it is a middleware chain only, not a server catalog.

## Binding an agent

An agent manifest references a gateway config by `id`:

```yaml
spec:
  llmGatewayRef: prod-llm-governance
  mcpGatewayRef: prod-mcp-governance
  mcpServers:
    - name: my-fetch-server
      transport: registered             # binding this server imports its full toolset
```

`POST /v1/agents` eagerly validates both refs against the live registries. Unknown ref → `422 urn:vais-agents:llm-gateway-ref-not-found` / `mcp-gateway-ref-not-found`.

**Binding a `transport: registered` server is sufficient to import its toolset.** No `tools[]` entry is required. If you want only a subset, add `tools: [name1, name2]` on the `mcpServers` entry (the allowlist), or add explicit `tools[]` entries (explicit mode).

**`McpGatewayRef` precedence (highest → lowest):**

1. Agent manifest `mcpGatewayRef` — replaces all lower tiers.
2. `McpServer` manifest `McpGatewayRef` — inherited when the agent omits its own; all bound `transport: registered` servers must carry the same ref (or none). Multiple distinct refs → `urn:vais-agents:mcp-gateway-ref-ambiguous`.
3. DI-registered global `ToolGatewayMiddleware` services — default fallback when no ref is set at either level.

## Apply order

Gateway configs must exist before the agent that binds to them:

```bash
vais apply -f llm-gateway.yaml        # LlmGatewayConfig
vais apply -f mcp-gateway.yaml        # McpGatewayConfig
vais apply -f fetch-server.yaml       # McpServer
vais apply -f my-agent.yaml           # Agent — refs validated here
```

## Live updates without redeployment

Updating a gateway config takes effect on the **next grain activation** (cache is evicted on re-apply). No agent manifest touch, no rolling restart:

```bash
# Increase rate limit from 30 to 60
vais apply -f mcp-gateway.yaml        # applied McpGatewayConfig prod-mcp-governance@1.0
# → next invocation of any agent bound to prod-mcp-governance picks up the new limit
```

## How it works: named middleware factory layer

Every gateway package ships a `services.AddNamed*GatewayMiddleware_<Name>()` DI extension. These registrations are collected by `DefaultLlmGatewayMiddlewareFactory` and `DefaultToolGatewayMiddlewareFactory` at startup.

When `AgentManifestTranslator` constructs a grain:

1. Fetches `LlmGatewayConfigManifest` from `ILlmGatewayConfigRegistry` by `llmGatewayRef`.
2. Calls `ILlmGatewayMiddlewareFactory.Create(spec)` for each entry in `spec.middleware`, passing `spec.params` as configuration.
3. Sets `StatefulAgentOptions.GatewayMiddleware` to the resulting chain.
4. Same process for `mcpGatewayRef` → `StatefulAgentOptions.ToolGatewayMiddleware`.

The `ToolWorkspacePolicy` entry is a sentinel: the translator intercepts it, constructs `ToolWorkspacePolicyMiddleware(workspacePolicies)` directly from the agent's `WorkspacePolicies` map, and never calls the factory for it.

## `transport: registered` and VirtualMcpToolSource

Server entries with `transport: registered` are resolved from `IMcpServerRegistry` at translation time rather than connected directly from the manifest URL. Two resolution paths:

- **Physical server** — delegates to `INamedToolSourceProvider` (e.g. a running Python plugin supervisor or `PhysicalMcpConnectionService`).
- **Virtual server** — `VirtualMcpToolSource` aggregates N upstream `IToolSource` instances with optional tool projection (`McpServerToolProjection`) and rename support.

**Import-all vs. explicit mode (D1).** For each `transport: registered` entry the translator applies a presence-gated rule:

- **Import-all** (default) — if no `tools[]` entry has `source: mcp:<serverName>`, the server's full discovered toolset is imported. This is the recommended path when binding a virtual server.
- **Explicit** — if at least one `tools[]` entry references the server via `source: mcp:<serverName>`, only those named tools are resolved (existing behavior, unchanged).

The `McpServerRef.Tools` allowlist (on the `mcpServers` entry) narrows the import in either mode. Allowlisted names that the server does not expose throw `urn:vais-agents:mcp-tool-not-found` at apply time.

Collision between two import-all servers on the same tool name throws `urn:vais-agents:mcp-tool-name-collision`; an explicit `tools[]` entry on one of them resolves the collision by switching that server to explicit mode.

## HTTP API

| Method | Path | Description |
|---|---|---|
| `POST` | `/v1/llm-gateways` | Apply `LlmGatewayConfig` |
| `GET` | `/v1/llm-gateways/{id}` | Fetch config |
| `DELETE` | `/v1/llm-gateways/{id}` | Delete config |
| `POST` | `/v1/mcp-gateways` | Apply `McpGatewayConfig` |
| `GET` | `/v1/mcp-gateways/{id}` | Fetch config |
| `DELETE` | `/v1/mcp-gateways/{id}` | Delete config |
| `POST` | `/v1/mcp-servers` | Apply `McpServer` |
| `GET` | `/v1/mcp-servers/{id}` | Fetch server |
| `DELETE` | `/v1/mcp-servers/{id}` | Delete server |

## Built-in middleware names

### LLM middleware (ship with `Core` and `Gateways.*` packages)

| Name | Package | What it does |
|---|---|---|
| `LlmLogging` | Core | Structured log per request/response (provider, model, token counts) |
| `LlmUsage` | Core | Accumulates token usage into `AgentContext` |
| `LlmOtel` | Core | OpenTelemetry span wrapping the provider call |
| `LlmPromptEnrichment` | Core | Injects dynamic system-prompt fragments |
| `Prometheus` | Gateways.Prometheus | `llm_requests_total`, `llm_request_duration_seconds`, `llm_tokens_total` |
| `Fallback` | Gateways.Fallback | Provider failover from `IFallbackProviderPool` |
| `SemanticCache` | Gateways.SemanticCache | Short-circuit repeated calls via `ISemanticCacheStore` |
| `StructuredOutput` | Gateways.StructuredOutput | JSON-schema output enforcement |

### Tool middleware

| Name | Package | What it does |
|---|---|---|
| `ToolLogging` | Core | Debug log per dispatch + outcome |
| `ToolOtel` | Core | OTel span per tool call |
| `ToolDenyFilter` | Core | Static block list — returns `ToolDenied` without calling next |
| `ToolResponseTruncation` | Core | Truncates results exceeding `maxCharacters` |
| `ToolRateLimit` | Gateways.McpGovernance | Sliding-window per-workspace-per-tool budget |
| `ToolWorkspacePolicy` | Gateways.McpGovernance | Per-workspace deny/allow prefix + privilege level |
| `ToolArgumentValidation` | Gateways.McpSecurity | Required-field presence check |
| `ToolOutputLengthGuard` | Gateways.McpSecurity | Hard reject over `maxCharacters` (no truncation) |
| `ToolRetry` | Gateways.McpReliability | Exponential backoff; skips non-retryable outcomes |
| `ToolTimeout` | Gateways.McpReliability | Per-dispatch deadline via `Task.WhenAny` |
| `ToolCircuitBreaker` | Gateways.McpReliability | Per-workspace failure-count circuit breaker |
| `ToolResultCache` | Gateways.McpCache | Deterministic key-based cache; skips error outcomes |
| `ToolJsonRepair` | Gateways.McpTransformation | Validates + attempts structural repair of JSON results |
| `ToolHtmlToMarkdown` | Gateways.McpTransformation | Strips HTML tags, decodes entities |

## See also

- [Declarative agents](declarative-agents.md) — how the translator turns a manifest into a running grain
- [Tools](tools.md) — `IToolSource`, `ITool`, `INamedToolSourceProvider`
- `samples/declarative-agent-mcp-gateways/` — runnable zero-C# example
