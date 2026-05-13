# Author an MCP gateway middleware

You'll write a `ToolGatewayMiddleware` subclass that intercepts every tool call your agents make, register it with DI, and verify it runs. End state: a `ToolLatencyAlertMiddleware` that times every dispatch and logs a structured WARN entry when latency exceeds a threshold — demonstrating the canonical wrap-around pattern (`await next()` in the middle, observation around it).

## The seam

`ToolGatewayMiddleware` (in `Vais.Agents.Abstractions`) is the abstract base. One override point — keep it small.

```csharp
public abstract class ToolGatewayMiddleware
{
    public virtual Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
        => next();
}
```

**Critical invariants:**

1. **Reentrant** — no per-call state in fields. Per-call state goes in local variables inside `InvokeAsync`.
2. **Short-circuit by not calling `next`.** Return a `ToolCallOutcome` with a non-null `Error` field — the model observes the error string and adapts; no exception bubbles up to the agent execution loop.
3. **The chain runs BEFORE `IToolGuardrail` hooks.** Guardrails remain as the inner layer for backwards compatibility.

## When to author a tool middleware vs. a tool guardrail

- **Middleware** — cross-cutting concerns over the dispatch path: logging, OTel, caching, rate limiting, retries, circuit breakers, transformation. Operates on every tool, mostly orthogonal to the tool's identity.
- **Guardrail** — per-tool semantic checks (e.g., "this tool requires the user to have role `admin`"). Guardrails are tightly tied to tool meaning; middleware is tightly tied to operational concerns.

When in doubt: middleware first. Use a guardrail only when the check is intrinsic to the tool's semantics, not to the call path.

## Prerequisites

- .NET 9 SDK.
- The reference impls in `src/Vais.Agents.Gateways.Mcp*` as templates — `McpCache`, `McpReliability`, `McpSecurity`, `McpTransformation`, `McpGovernance`.

## 1. Implement the middleware

`ToolLatencyAlert.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Vais.Agents;

namespace MyApp.Middleware;

/// <summary>
/// Records elapsed dispatch time for every tool call.
/// Logs at WARN when latency exceeds a configured threshold.
/// Always passes through — never short-circuits.
/// </summary>
public sealed class ToolLatencyAlertMiddleware : ToolGatewayMiddleware
{
    private readonly ILogger<ToolLatencyAlertMiddleware> _logger;
    private readonly TimeSpan _slowThreshold;

    public ToolLatencyAlertMiddleware(
        ILogger<ToolLatencyAlertMiddleware> logger,
        TimeSpan? slowThreshold = null)
    {
        _logger = logger;
        _slowThreshold = slowThreshold ?? TimeSpan.FromSeconds(5);
    }

    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();           // per-call, local variable — reentrant-safe
        ToolCallOutcome outcome;
        try
        {
            outcome = await next();
        }
        finally
        {
            sw.Stop();
        }

        if (sw.Elapsed > _slowThreshold)
        {
            _logger.LogWarning(
                "Slow tool dispatch: {ToolName} took {ElapsedMs} ms (threshold {ThresholdMs} ms)",
                context.ToolName,
                sw.ElapsedMilliseconds,
                _slowThreshold.TotalMilliseconds);
        }

        return outcome;
    }
}
```

The `Stopwatch` is allocated per-call (local variable), so concurrent invocations don't corrupt each other's timing. The middleware always calls `next` — it's an observer, never a short-circuit.

## 2. A short-circuit example for contrast

Sometimes you do want to short-circuit. Example: a tenant-specific deny list that supplements the `ToolDenyFilterMiddleware`'s static list:

```csharp
using System.Collections.Frozen;
using Vais.Agents;

namespace MyApp.Middleware;

public sealed class TenantToolDenyMiddleware : ToolGatewayMiddleware
{
    private readonly IReadOnlyDictionary<string, FrozenSet<string>> _denyByTenant;

    public TenantToolDenyMiddleware(IReadOnlyDictionary<string, IReadOnlyList<string>> denyByTenant)
    {
        _denyByTenant = denyByTenant.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }

    public override Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        var tenant = context.AgentContext?.TenantId ?? "_unknown";
        if (_denyByTenant.TryGetValue(tenant, out var denied)
            && denied.Contains(context.ToolName))
        {
            // Short-circuit. Return an outcome with Error set; next is never called.
            return Task.FromResult(new ToolCallOutcome(
                CallId: context.CallId,
                Result: null,
                Error: $"ToolDenied: tenant '{tenant}' is not allowed to call '{context.ToolName}'."));
        }

        return next();
    }
}
```

Note the field — a `FrozenSet<string>` per tenant — is set in the constructor and read-only thereafter. That's reentrant-safe; only per-call mutable state would violate the invariant.

## 3. Register with DI

```csharp
services.AddSingleton<ToolGatewayMiddleware>(sp =>
    new ToolLatencyAlertMiddleware(
        sp.GetRequiredService<ILogger<ToolLatencyAlertMiddleware>>(),
        slowThreshold: TimeSpan.FromSeconds(3)));
```

For multiple registrations the order matters — first registered is outermost in the chain. Or expose a DI extension method matching the shipped middleware pattern:

```csharp
public static class ToolLatencyAlertServiceCollectionExtensions
{
    public static IServiceCollection AddToolLatencyAlert(
        this IServiceCollection services,
        TimeSpan? slowThreshold = null)
    {
        services.AddSingleton<ToolGatewayMiddleware>(sp =>
            new ToolLatencyAlertMiddleware(
                sp.GetRequiredService<ILogger<ToolLatencyAlertMiddleware>>(),
                slowThreshold));
        return services;
    }
}

// Usage:
services.AddToolLatencyAlert(slowThreshold: TimeSpan.FromSeconds(3));
```

## 4. Verify

```csharp
[Fact]
public async Task LogsWarnOnSlowDispatch()
{
    var logger = new TestLogger<ToolLatencyAlertMiddleware>();
    var mw = new ToolLatencyAlertMiddleware(logger, slowThreshold: TimeSpan.FromMilliseconds(10));

    var ctx = new ToolGatewayContext("slow_tool", callId: "1", arguments: default, agentContext: null);
    var outcome = await mw.InvokeAsync(ctx,
        next: async () =>
        {
            await Task.Delay(50);
            return new ToolCallOutcome("1", "ok", null);
        });

    outcome.Result.Should().Be("ok");
    logger.Entries.Should().ContainSingle(e =>
        e.Level == LogLevel.Warning && e.Message.Contains("Slow tool dispatch"));
}

[Fact]
public async Task NoLogOnFastDispatch()
{
    var logger = new TestLogger<ToolLatencyAlertMiddleware>();
    var mw = new ToolLatencyAlertMiddleware(logger, slowThreshold: TimeSpan.FromSeconds(5));

    var ctx = new ToolGatewayContext("fast_tool", callId: "1", arguments: default, agentContext: null);
    var outcome = await mw.InvokeAsync(ctx,
        next: () => Task.FromResult(new ToolCallOutcome("1", "ok", null)));

    logger.Entries.Should().BeEmpty();
}
```

## Composition rules

Like the LLM gateway, middleware composes right-to-left. First registered = outermost. A typical stack:

```csharp
services.AddToolWorkspacePolicyMiddleware(policies);   // outermost: policy decides
services.AddToolDenyFilterMiddleware(blocked);         // static deny list
services.AddToolCircuitBreakerMiddleware(5, ...);      // circuit breaker
services.AddToolTimeoutMiddleware(10s);                // deadline
services.AddToolRetryMiddleware(3, 200ms);             // retry under breaker
services.AddToolResultCacheMiddleware(cache);          // cache hit short-circuits
services.AddToolLatencyAlert(3s);                      // your middleware — observation
services.AddToolLoggingMiddleware();                   // logs
services.AddToolOtelMiddleware();                      // OTel spans (innermost)
```

The cache short-circuits when there's a hit; the latency alert still runs on a cache miss because it sits below the cache (closer to the tool). Order placement is a design choice — observation middlewares usually want to sit close to the inner edge.

## Composition with guardrails

`IToolGuardrail` hooks run **inside** the gateway chain, after every middleware has had a chance to short-circuit. Guardrails were the original tool-gating mechanism and remain for backwards compatibility. New code prefers middleware:

- Short-circuit capability is built in (just don't call `next`).
- Reentrancy and chain composition are well-defined.
- Logging / OTel / metric integration is uniform with the LLM gateway.

If you have an existing `IToolGuardrail` that wants short-circuit semantics, port it to a `ToolGatewayMiddleware` subclass.

## What you built

- A `ToolGatewayMiddleware` subclass that wraps every tool dispatch.
- A reentrant implementation that uses local variables for per-call state.
- Two patterns demonstrated: pure observation (latency alert) and conditional short-circuit (tenant deny).

## Next

- **[Other extension seams](other-extension-seams.md)** — input middleware, guardrails, completion providers, session stores, predicate operators, policy engines, event subscribers.
- **[Author an LLM gateway middleware](author-an-llm-gateway-middleware.md)** — the LLM-side mirror.
- [Full MCP middleware catalog](../guides/gate-tool-calls-with-the-tool-gateway.md) — every shipped middleware (cache, reliability, security, transformation, governance).
- [Concepts → Tools](../concepts/tools.md) — `ITool`, `IToolRegistry`, `IToolGuardrail`, `DefaultToolCallDispatcher`.
