# Guide: plug in gateway middleware

Gateway middleware intercepts every LLM call — before it reaches the provider and after it returns — without touching the provider or agent code. It operates on both the non-streaming and streaming paths. One `LlmGatewayMiddleware` instance can cover logging, caching, rate limiting, fallback, and output validation all at once.

## How middleware is ordered

`StatefulAgentOptions.GatewayMiddleware` is a list. The framework builds the call chain **right-to-left**: index 0 is the outermost filter (first to intercept on the way in, last to see the response on the way out).

```text
request → [0] outer → [1] inner → provider
response ← [0] outer ← [1] inner ← provider
```

A middleware at index 0 that does not call `next` short-circuits the entire chain — the provider is never reached. Use this ordering rule when combining multiple middlewares.

## LlmMockMiddleware — unit-test agents without a real LLM

```csharp
using Vais.Agents.Gateways.Testing;

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    GatewayMiddleware =
    [
        new LlmMockMiddleware(
            new CompletionResponse("I am a mock response.", ModelId: "test-model"),
            new CompletionResponse("Second turn."),
        ),
    ],
});

var r1 = await agent.AskAsync("hello");   // "I am a mock response."
var r2 = await agent.AskAsync("again");  // "Second turn."
```

The mock does not call the real provider. The provider argument is still required (for type safety), but it is never invoked when the mock queue has entries.

## LlmJsonOutputMiddleware — enforce structured output

```csharp
using Vais.Agents.Gateways.StructuredOutput;

sealed record SentimentResult(string Label, double Score);

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    GatewayMiddleware =
    [
        new LlmJsonOutputMiddleware<SentimentResult>(),
        // provider sits behind the validation layer
    ],
    SystemPrompt = "Always respond with JSON matching {\"Label\": \"...\", \"Score\": 0.0}.",
});
```

If the LLM returns text that cannot be deserialized as `SentimentResult`, the middleware throws `AgentGuardrailDeniedException(GuardrailLayer.Output, ...)`. The default resilience pipeline retries — disable it with `ResiliencePipeline = Polly.ResiliencePipeline.Empty` to fail fast instead.

## LlmSemanticCacheMiddleware — cache repeated prompts

```csharp
using Vais.Agents.Gateways.SemanticCache;

var store = new InMemorySemanticCacheStore();

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    GatewayMiddleware = [new LlmSemanticCacheMiddleware(store)],
});

var r1 = await agent.AskAsync("capital of France");  // hits provider, stores result
var r2 = await agent.AskAsync("capital of France");  // returns cached result, provider not called
```

The default cache key is the last user turn's text. Implement `ISemanticCacheStore` to use a vector-similarity backend for fuzzy matching.

## LlmRateLimitMiddleware — enforce per-key budgets

```csharp
using Vais.Agents.Gateways.Governance;

var store = new InMemorySlidingWindowRateLimitStore();
var options = new RateLimitOptions
{
    MaxRequestsPerWindow = 10,
    MaxTokensPerWindow = 50_000,
    Window = TimeSpan.FromMinutes(1),
};
var accessor = new AsyncLocalAgentContextAccessor();

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    GatewayMiddleware = [new LlmRateLimitMiddleware(store, options, accessor)],
    ContextAccessor = accessor,
});

using var _ = accessor.Push(new AgentContext { UserId = "user-123" });
await agent.AskAsync("hello");  // counted under "user-123"
```

The middleware uses `AgentContext.UserId` as the rate-limit key (falls back to `TenantId`, then `WorkspaceId`, then `AgentName`, then `"global"`). Implement `IRateLimitStore` over Redis for distributed enforcement across replicas.

## LlmFallbackMiddleware — automatic provider failover

```csharp
using Vais.Agents.Gateways.Fallback;

var pool = new InMemoryFallbackProviderPool(
    primaryProvider,     // tried first
    backupProvider,      // tried if primary throws
);

var agent = new StatefulAiAgent(primaryProvider, new StatefulAgentOptions
{
    GatewayMiddleware = [new LlmFallbackMiddleware(pool)],
    ResiliencePipeline = Polly.ResiliencePipeline.Empty, // middleware handles retries
});
```

On the streaming path, fallback commits after the first successful delta arrives — it does not retry a stream mid-delivery.

## LlmLoadBalancingMiddleware — round-robin across providers

```csharp
var pool = new InMemoryFallbackProviderPool(providerA, providerB, providerC);

var agent = new StatefulAiAgent(providerA, new StatefulAgentOptions
{
    GatewayMiddleware = [new LlmLoadBalancingMiddleware(pool)],
});
```

Each call increments an internal counter and picks `counter % pool.Count`. Thread-safe via `Interlocked.Increment`.

## Combining multiple middlewares

Middlewares compose left-to-right in `GatewayMiddleware`. The outermost (index 0) runs first. A typical production stack:

```csharp
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    GatewayMiddleware =
    [
        new LlmRateLimitMiddleware(rateLimitStore, rateLimitOptions, accessor),  // enforces budget
        new LlmSemanticCacheMiddleware(cacheStore),                              // short-circuits on hit
        new LlmLoggingMiddleware(logger),                                        // logs miss path
        new LlmFallbackMiddleware(pool),                                         // fallback on miss
    ],
});
```

Execution order on a cache miss:
1. Rate limit check — throws if budget exceeded
2. Cache lookup — miss, continues
3. Log the request
4. Try providers in fallback order

On a cache hit:
1. Rate limit check
2. Cache hit — returns immediately; steps 3 and 4 are never reached

## DI registration

Each gateway package ships a DI extension for use with the manifest translator. Register in `IServiceCollection`:

```csharp
services.AddLlmLoggingMiddleware();          // Core
services.AddLlmUsageMiddleware();            // Core
services.AddLlmOtelMiddleware();             // Core
services.AddLlmSemanticCacheMiddleware();    // Gateways.SemanticCache
services.AddLlmRateLimitMiddleware(options); // Gateways.Governance
services.AddLlmFallbackMiddleware(pool);     // Gateways.Fallback
services.AddLlmLoadBalancingMiddleware(pool);// Gateways.Fallback
```

Registered middleware is automatically prepended to the filter chains of all agents instantiated by `IAgentManifestTranslator`. The registration order determines the chain order (first registered = outermost).

## See also

- [Reference: packages](../reference/packages.md) — full package descriptions for each `Gateways.*` package.
- [Add input and output guardrails](add-input-output-guardrails.md) — per-turn guardrails that run outside the gateway layer.
- [Deploy OTel and Langfuse](deploy-otel-and-langfuse.md) — `LlmOtelMiddleware` produces spans consumed by OTel collectors.
