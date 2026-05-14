# Wire the LLM gateway

You'll declare a reusable `LlmGatewayConfig` manifest and bind your agent to it. Every model call your agent makes flows through the middleware chain — logging, OTel spans, Prometheus metrics — without any C# code. End state: every `vais invoke` against your agent emits a structured log line and an OTel span you can see in the runtime container logs.

## Why this matters

The gateway is the only path for LLM calls. Observability, rate limiting, fallback, structured-output validation, and provider routing all live in middleware — never scattered across agent code. Adding a new cross-cutting concern is a single manifest change, not a search-and-replace.

## Prerequisites

- A running runtime reachable at `http://localhost:8080` ([DevOps section](../devops/index.md)).
- The CLI pointed at it (`vais config use-context local`).
- An agent registered with the runtime — see [Your first declarative agent](your-first-declarative-agent.md).
- `OPENAI_API_KEY` exported.

## Step 1 — Declare the gateway config

Save as `observable-llm-gateway.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: LlmGatewayConfig
metadata:
  id: observable-llm-gateway
  version: "1.0"
  description: Structured logging + OTel + Prometheus on every LLM call.
spec:
  middleware:
    - name: LlmLogging
    - name: LlmOtel
    - name: Prometheus
```

**What these middleware do:**

- `LlmLogging` — one structured `Information`-level log line per call with `model`, `promptTokens`, `completionTokens`, and `latencyMs`.
- `LlmOtel` — one `llm.completion` OTel span per call (or `llm.completion.stream` on the streaming path). Wraps provider duration; tagged with `gen_ai.*` semantic conventions plus `vais.*` extensions.
- `Prometheus` — increments OTel metrics that Prometheus can scrape: `gen_ai_client_token_usage_tokens` (by `gen_ai_token_type`) and `gen_ai_client_operation_duration_seconds`. Available at `GET /metrics` when `VAIS_OTEL_ENDPOINT` or `VAIS_OTEL_CONSOLE=true` is set.

Order matters — middleware runs **left-to-right**: the first entry is the outermost and sees the request first / response last. For observability middleware the order is rarely consequential; for rate limiting and fallback it is.

Apply:

```bash
vais apply -f observable-llm-gateway.yaml
```

## Step 2 — Bind your agent to it

Edit your existing agent manifest and add `llmGatewayRef`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: greeter
  version: "1.0"
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: "Be friendly and concise."
  handler:
    typeName: declarative
  protocols:
    - kind: Http
  llmGatewayRef: observable-llm-gateway     # ← add this
  tools: []
```

`vais apply` validates `llmGatewayRef` eagerly — applying an agent that references a non-existent gateway config fails fast. Apply order matters: gateway first, then agent.

```bash
vais apply -f greeter.yaml
```

## Step 3 — Invoke and observe

```bash
vais invoke greeter --text "Hello!"
docker logs -f vais-runtime
```

In the runtime logs you'll see, for that single invocation:

- A `LlmLogging` `Information` line: `LLM call: model=gpt-4o-mini promptTokens=… completionTokens=… latencyMs=…`
- The corresponding `llm.completion` OTel span — visible in the console if `VAIS_OTEL_CONSOLE=true`, otherwise exported to your collector via `VAIS_OTEL_ENDPOINT`.
- OTel metrics incremented at `GET /metrics`: `gen_ai_client_token_usage_tokens` and `gen_ai_client_operation_duration_seconds`.

## Step 4 — Add a more interesting middleware

Two examples worth wiring next:

### Rate limiting (`LlmRateLimit`)

```yaml
spec:
  middleware:
    - name: LlmRateLimit
      params:
        maxRequestsPerWindow: 10
        windowSeconds: 60
    - name: LlmLogging
    - name: LlmOtel
```

Per-user, per-workspace, or global. The key is resolved from `AgentContext` (`UserId` → `TenantId` → `WorkspaceId` → `AgentName` → `"global"`). Excess calls fail with a 429 response. `maxTokensPerWindow` is an optional additional cap.

### Fallback across providers (`Fallback`)

```yaml
spec:
  middleware:
    - name: LlmLogging
    - name: Fallback
      params:
        pool:
          - provider: openai
            id: gpt-4o
            apiKeyRef: secret://env/OPENAI_API_KEY
          - provider: anthropic
            id: claude-3-haiku-20240307
            apiKeyRef: secret://env/ANTHROPIC_API_KEY
```

On a provider failure (timeout, 5xx, transient error), the middleware tries the next entry in `pool` in order. Each pool entry is a full model spec — the same fields as `spec.model`. On the streaming path, fallback commits to the first provider that delivers at least one delta; it does not retry a stream already in progress.

## What you built

- A reusable `LlmGatewayConfig` that any agent in the runtime can bind to via `llmGatewayRef`.
- Every model call your agent makes now flows through logging, OTel, and Prometheus uniformly.
- Adding a new cross-cutting concern (rate limit, fallback, semantic cache, structured-output validation, custom redaction) is a single line in the gateway manifest plus `vais apply`. No agent code changes.

## Next

- **[Wire the MCP gateway](wire-the-mcp-gateway.md)** — same shape, but for tool calls.
- [Full LLM middleware catalog](../guides/plug-in-gateway-middleware.md) — every shipped middleware: caching, structured output, load balancing, mock-for-tests, more.
- [Concepts → Gateway config control plane](../concepts/gateway-config-control-plane.md) — how `LlmGatewayConfig` manifests are stored, versioned, and composed.
- [Concepts → Observability](../concepts/observability.md) — the full OTel + Langfuse pipeline.
