# McpGatewayMiddleware

Compose MCP tool gateway middleware in C# — retry, deterministic cache, argument validation. Three scripted passes that invoke the middleware pipeline directly with a `ToolGatewayContext`; no agent loop or API key required.

## Prerequisites

The v0.40 gateway packages are in the source tree but must be packed before they appear in the local feed:

```bash
dotnet pack src/Vais.Agents.Gateways.McpReliability -o artifacts/packages/
dotnet pack src/Vais.Agents.Gateways.McpCache       -o artifacts/packages/
dotnet pack src/Vais.Agents.Gateways.McpSecurity    -o artifacts/packages/
```

## Run

```bash
dotnet run --project samples/McpGatewayMiddleware
```

## Expected output

```
== 1 — ToolRetryMiddleware (fails twice → succeeds on 3rd attempt) ==
  result:   "{"temp":18,"condition":"Sunny"}"
  error:    (none)
  attempts: 3  (expected 3)

== 2 — ToolResultCacheMiddleware (same args → cache hit on 2nd call) ==
  call 1: "{"temp":18}"
  call 2: "{"temp":18}"  (from cache)
  tool invoked: 1 time(s)  (expected 1)
  bodies match: True

== 3 — ToolArgumentValidationMiddleware (missing arg → ToolDenied) ==
  with 'location': error=(none)  tool called=True
  missing arg:     error=ToolDenied  tool called=False

Done.
```

## What it demonstrates

- `StatefulAgentOptions.ToolGatewayMiddleware` — `IReadOnlyList<ToolGatewayMiddleware>` on `StatefulAgentOptions`. Each item intercepts every tool-call dispatch before the actual tool executes.
- `ToolGatewayMiddleware.InvokeAsync(ToolGatewayContext, Func<Task<ToolCallOutcome>>, CancellationToken)` — the single abstract method. `next` is a zero-arg closure capturing the context; compose pipelines by building nested closures from innermost to outermost.
- `ToolRetryMiddleware(maxAttempts, initialDelay)` — retries when `outcome.Error != null`, using exponential backoff (`initialDelay × 2^attempt`). Hard-stops on sentinel errors `"ToolDenied"`, `"CircuitOpen"`, `"ToolRateLimitExceeded"` — these signal intentional denial or system state, not transient failures.
- `ToolResultCacheMiddleware(IToolResultCache, excludedTools?)` — caches successful outcomes (null `Error`) keyed on `(toolName, arguments)`. On cache hit returns the stored outcome with the current call's `CallId`. `InMemoryToolResultCache` is a zero-config in-process store.
- `ToolArgumentValidationMiddleware(IReadOnlyDictionary<string, IReadOnlyList<string>>)` — validates required argument names are present in `context.Arguments` before calling `next`. Returns `ToolCallOutcome { Error = "ToolDenied" }` on violation without calling `next`. The sentinel `"ToolDenied"` error causes `ToolRetryMiddleware` (if upstream) to stop retrying immediately.
- `ToolGatewayContext(toolName, callId, arguments, agentContext)` — immutable record passed to every middleware. `AgentContext.Empty` is a no-op for samples that don't need tenant/correlation context.
- `ToolCallOutcome(CallId, Result, Error?)` — `Error == null` signals success; non-null `Error` signals failure with a reason string.

## Docs

- [MCP gateway concept](../../docs/concepts/mcp-gateway.md)
- [`LlmGatewayMiddleware`](../LlmGatewayMiddleware) — companion sample for LLM gateway middleware
- [`declarative-agent-mcp-gateways`](../declarative-agent-mcp-gateways) — same middleware pipelines via YAML manifests
