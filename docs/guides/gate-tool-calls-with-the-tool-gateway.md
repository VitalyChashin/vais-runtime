# Gate tool calls with the Tool Gateway

The Tool Gateway is a middleware pipeline that intercepts every tool call — before it reaches the tool and after it returns — without touching tool implementations or agent code. Wire middleware into `StatefulAgentOptions.ToolGatewayMiddleware` to add logging, retries, circuit breaking, caching, rate limiting, security validation, and response transformation to all tool calls uniformly.

## How the Tool Gateway differs from the LLM Gateway

Both gateways compose middleware using the same right-to-left chain — index 0 is outermost. The key difference is where they intercept:

| | LLM Gateway | Tool Gateway |
|---|---|---|
| Intercepts | LLM completion calls | Tool invocations |
| Input type | `CompletionRequest` | `ToolGatewayContext` |
| Output type | `CompletionResponse` | `ToolCallOutcome` |
| Short-circuit return | Alternative `CompletionResponse` | `ToolCallOutcome` with an `Error` field |
| Base class | `LlmGatewayMiddleware` | `ToolGatewayMiddleware` |

A short-circuiting tool middleware returns a `ToolCallOutcome` with a non-null `Error` string. The model observes the error and can adapt its plan — no exception is thrown to the execution loop.

## How middleware is ordered

`StatefulAgentOptions.ToolGatewayMiddleware` is a list. The chain is built right-to-left: index 0 is the outermost filter (first to intercept on the way in, last to see the response on the way out).

```text
tool call → [0] outer → [1] inner → DefaultToolCallDispatcher → ITool
tool result ← [0] outer ← [1] inner ← DefaultToolCallDispatcher ← ITool
```

A middleware at index 0 that does not call `next` prevents the tool from being invoked. Use this ordering rule when combining deny filters and caches with logging.

## Reference middleware in `Vais.Agents.Core`

All four reference plugins ship in `Vais.Agents.Core` and are zero-dependency (no extra NuGet).

### ToolLoggingMiddleware — debug-level dispatch tracing

```csharp
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolLoggingMiddleware(logger),
    ],
});
```

Emits two `LogLevel.Debug` messages per call: one when the tool is dispatched (includes tool name and call ID), one when it returns (indicates success or the error code). Never mutates the outcome.

### ToolOtelMiddleware — per-call OTel spans

```csharp
using Vais.Agents.Core;

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolOtelMiddleware(),
    ],
});
```

Starts an `Activity` named `tool.gateway/{toolName}` on `AgenticDiagnostics.ActivitySource` and tags it with `vais.tool.name`, `vais.tool.call_id`, and `vais.workspace.id`. Sets `ActivityStatusCode.Ok` on success and `ActivityStatusCode.Error` on error outcomes or exceptions. On exception, also sets `vais.error.type` to the exception type name and rethrows. Safe when no listener is attached — `StartActivity` returns null and the middleware adds zero overhead.

### ToolDenyFilterMiddleware — static block list

```csharp
using Vais.Agents.Core;

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolDenyFilterMiddleware(["delete_files", "exec_shell"]),
    ],
});
```

Compares each incoming tool name against the block list using case-insensitive exact matching. Blocked calls return immediately with `Error = "ToolDenied"` and a human-readable result string — `next` is never called. Complementary to the dynamic `AgentContext.AllowedTools` allow-list in `DefaultToolCallDispatcher`; the deny filter applies a static configuration layer on top.

### ToolResponseTruncationMiddleware — context window protection

```csharp
using Vais.Agents.Core;

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolResponseTruncationMiddleware(maxCharacters: 8192),
    ],
});
```

Truncates tool results that exceed `maxCharacters` (default: 4096) and appends `[Truncated: response exceeded N characters]` so the model knows the result was cut. Error outcomes are never truncated — error messages must be fully visible to the model. Truncation is by character count, not tokens, to avoid a tokenizer dependency.

## Plugin packages

Five optional packages extend the gateway with reliability, caching, governance, security, and transformation capabilities. Each package depends only on `Vais.Agents.Abstractions`.

### `Vais.Agents.Gateways.McpReliability` — retries, timeouts, circuit breaking

```csharp
using Vais.Agents.Gateways.McpReliability;

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolCircuitBreakerMiddleware(
            failureThreshold: 5,
            openDuration: TimeSpan.FromSeconds(30)),
        new ToolTimeoutGuard(
            timeout: TimeSpan.FromSeconds(10)),
        new ToolRetryMiddleware(
            maxAttempts: 3,
            baseDelay: TimeSpan.FromMilliseconds(200)),
    ],
});
```

**`ToolRetryMiddleware`** — retries failed calls up to `maxAttempts` with exponential backoff starting at `baseDelay`. Non-retryable errors (`ToolDenied`, `CircuitOpen`, `ToolRateLimitExceeded`) are returned immediately without retrying.

**`ToolTimeoutGuard`** — enforces a per-dispatch deadline. When `next` does not complete within `timeout`, returns `Error = "ToolTimeout"` instead of throwing. Outer cancellation propagates as `OperationCanceledException` as normal.

**`ToolCircuitBreakerMiddleware`** — tracks failures per `WorkspaceId` (falls back to a global circuit when the workspace is empty). After `failureThreshold` consecutive failures, the circuit opens and all calls return `Error = "CircuitOpen"` for `openDuration` without touching the tool. `ToolDenied` and `CircuitOpen` outcomes are not counted as failures. Thread-safe.

Ordering above — circuit-breaker outermost, retry innermost — ensures retries happen inside the breaker window: individual failures decrement the retry budget without immediately opening the circuit.

### `Vais.Agents.Gateways.McpCache` — deterministic result cache

```csharp
using Vais.Agents.Gateways.McpCache;

var cache = new InMemoryToolResultCache();
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolResultCacheMiddleware(
            cache,
            excludedTools: ["send_email", "charge_card"]),
    ],
});
```

**`ToolResultCacheMiddleware`** — on a cache miss, calls `next` and stores the result when the outcome has no error. On a cache hit, returns the cached outcome with the current `CallId`. Tools in `excludedTools` always bypass the cache (side-effectful tools must not be cached). Cache key: `{toolName}:{arguments}` where arguments is the normalized JSON representation of the call's `JsonElement`.

**`InMemoryToolResultCache`** — thread-safe `ConcurrentDictionary` backing. Implement `IToolResultCache` for Redis or other distributed stores.

### `Vais.Agents.Gateways.McpGovernance` — rate limiting and workspace policy

```csharp
using Vais.Agents.Gateways.McpGovernance;

var store = new InMemorySlidingWindowRateLimitStore();

var policies = new Dictionary<string, WorkspaceToolPolicy>
{
    ["ws-prod"] = new WorkspaceToolPolicy(
        AllowedPrefixes: ["search_", "read_"],
        DeniedPrefixes: ["admin_"],
        MinPrivilegeLevel: (int)PrivilegeLevel.Workspace),
};

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolWorkspacePolicyMiddleware(policies),
        new ToolRateLimitMiddleware(store, new ToolRateLimitOptions
        {
            MaxRequestsPerWindow = 60,
            Window = TimeSpan.FromMinutes(1),
        }),
    ],
});
```

**`ToolRateLimitMiddleware`** — enforces a per-workspace per-tool sliding-window request budget using `IRateLimitStore.RecordAndGetAsync`. Returns `Error = "ToolRateLimitExceeded"` when the budget is exhausted. Key: `tool:{workspaceId}:{toolName}` (falls back to `_global` when workspace is empty). Implement `IRateLimitStore` (from `Vais.Agents.Gateways.Governance`) for distributed enforcement.

**`ToolWorkspacePolicyMiddleware`** — looks up the workspace's `WorkspaceToolPolicy` by `AgentContext.WorkspaceId`. Absent policies pass through. `WorkspaceToolPolicy.IsAllowed` checks deny prefixes first, then allow prefixes (empty allow list = all non-denied tools are allowed), then `MinPrivilegeLevel` — where `PrivilegeLevel.Platform = 0` (highest) and `PrivilegeLevel.Agent = 2` (lowest), so callers with a numerically higher level value than `MinPrivilegeLevel` are denied.

### `Vais.Agents.Gateways.McpSecurity` — argument validation and output size guard

```csharp
using Vais.Agents.Gateways.McpSecurity;

// Require "query" on search tools, "path" on file tools:
var required = new Dictionary<string, IReadOnlyList<string>>
{
    ["search"] = ["query"],
    ["read_file"] = ["path"],
};

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolArgumentValidationMiddleware(required),
        new ToolOutputLengthGuard(maxCharacters: 32_000),
    ],
});
```

**`ToolArgumentValidationMiddleware`** — checks that every required field is present in the call's `JsonElement.Arguments`. Returns `Error = "ToolDenied"` with a list of missing fields when validation fails, without calling `next`.

**`ToolOutputLengthGuard`** — rejects (does not truncate) responses that exceed `maxCharacters` with `Error = "ToolOutputTooLarge"`. Distinct from `ToolResponseTruncationMiddleware` in `Core`: the guard is a hard limit enforced as a policy; truncation is a best-effort size reducer. Use the guard when the downstream consumer cannot handle large inputs; use truncation when you want the model to see a partial result.

### `Vais.Agents.Gateways.McpTransformation` — response normalisation

```csharp
using Vais.Agents.Gateways.McpTransformation;

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolJsonRepairMiddleware(),
        new ToolHtmlToMarkdownMiddleware(),
    ],
});
```

**`ToolJsonRepairMiddleware`** — validates that the tool result is well-formed JSON. On a parse error it attempts a structural repair (stub in the current release; safe no-op when repair is not possible). Error outcomes pass through unchanged.

**`ToolHtmlToMarkdownMiddleware`** — detects HTML responses (starts with `<`, contains tag-like content) and strips HTML tags via regex, then decodes HTML entities (`WebUtility.HtmlDecode`). The output is plain text suitable for LLM context windows. Error outcomes and non-HTML responses pass through unchanged.

## Combining multiple middlewares

```csharp
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    ToolRegistry = registry,
    ToolGatewayMiddleware =
    [
        new ToolWorkspacePolicyMiddleware(policies),    // [0] outermost: policy first
        new ToolDenyFilterMiddleware(blockedTools),     // [1] static block list
        new ToolCircuitBreakerMiddleware(5, TimeSpan.FromSeconds(30)),  // [2] circuit
        new ToolTimeoutGuard(TimeSpan.FromSeconds(10)),                 // [3] deadline
        new ToolRetryMiddleware(3, TimeSpan.FromMilliseconds(200)),     // [4] retry
        new ToolResultCacheMiddleware(cache, excludedTools),            // [5] cache
        new ToolLoggingMiddleware(logger),              // [6] log (after policy/deny, before tool)
        new ToolOtelMiddleware(),                       // [7] innermost: spans
    ],
});
```

Execution order on a cache miss:
1. Policy check — denied by workspace rule? Return `ToolDenied`. Otherwise continue.
2. Deny filter — blocked by static list? Return `ToolDenied`. Otherwise continue.
3. Circuit breaker — circuit open? Return `CircuitOpen`. Otherwise continue.
4. Timeout — start the deadline clock.
5. Retry — call the next layer up to 3 times.
6. Cache — miss, calls the tool.
7. Log — log dispatch and outcome.
8. OTel — emit the span.
9. Tool — `ITool.InvokeAsync`.

On a cache hit (step 6), the cache returns immediately; steps 7–9 are still reached on the way back but the tool itself is never called.

## DI registration

Each gateway package ships a DI extension. Use these when the manifest translator instantiates agents from YAML declarations:

```csharp
// Core reference plugins
services.AddToolLoggingMiddleware();
services.AddToolOtelMiddleware();
services.AddToolDenyFilterMiddleware(["exec_shell", "delete_files"]);
services.AddToolResponseTruncationMiddleware(maxCharacters: 8192);

// Plugin packages
services.AddToolRetryMiddleware();                          // McpReliability
services.AddToolTimeoutMiddleware(TimeSpan.FromSeconds(10));
services.AddToolCircuitBreakerMiddleware(5, TimeSpan.FromSeconds(30));
services.AddInMemoryToolResultCache();                     // McpCache — also registers IToolResultCache
services.AddToolResultCacheMiddleware();
services.AddToolRateLimitMiddleware(options);              // McpGovernance
services.AddToolWorkspacePolicyMiddleware(policies);
services.AddToolArgumentValidationMiddleware(requiredArgs); // McpSecurity
services.AddToolOutputLengthGuard(maxCharacters: 32_000);
services.AddToolJsonRepairMiddleware();                    // McpTransformation
services.AddToolHtmlToMarkdownMiddleware();
```

Registered middleware is automatically picked up by `IAgentManifestTranslator` and prepended to every declarative agent's tool dispatch chain. Registration order determines the chain order (first registered = outermost, same as `ToolGatewayMiddleware` list order).

## See also

- [Guide: plug in gateway middleware](plug-in-gateway-middleware.md) — the LLM Gateway, which intercepts completion calls rather than tool calls.
- [Concept: tools](../concepts/tools.md) — `ITool`, `IToolRegistry`, `IToolGuardrail`, and `DefaultToolCallDispatcher`.
- [Reference: packages](../reference/packages.md) — `Vais.Agents.Gateways.Mcp*` package descriptions.
- [Add input and output guardrails](add-input-output-guardrails.md) — guardrails run inside `DefaultToolCallDispatcher`, before the gateway chain is even entered.
