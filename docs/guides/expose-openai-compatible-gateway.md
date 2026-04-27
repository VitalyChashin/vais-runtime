# Guide: expose an OpenAI-compatible gateway

`Vais.Agents.Gateways.OpenAiCompat` turns any `IModelRouter` + `LlmGatewayMiddleware` chain into an HTTP endpoint that speaks the OpenAI chat-completions wire format. Any client built against the OpenAI SDK — Python `openai`, TypeScript `openai`, LiteLLM, etc. — can target it without modification.

## Endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/v1/chat/completions` | Non-streaming (`"stream": false`) and streaming SSE (`"stream": true`) |
| `GET` | `/v1/models` | Lists models from the `IModelRouter` route table |

## Minimal setup

```csharp
using Vais.Agents.Core;
using Vais.Agents.Gateways.OpenAiCompat;

var builder = WebApplication.CreateBuilder(args);

// 1. Register gateway services
builder.Services.AddOpenAiCompatGateway();

// 2. Register identity resolver (pass-through = no auth)
builder.Services.AddPassThroughIdentityResolver();

// 3. Register model router
var routes = new Dictionary<string, ModelRoute>
{
    ["gpt-4o"] = new ModelRoute(myOpenAiProvider, new ModelSpec { ModelId = "gpt-4o" }),
    ["claude-3-5-sonnet"] = new ModelRoute(myAnthropicProvider, new ModelSpec { ModelId = "claude-3-5-sonnet-20241022" }),
};
builder.Services.AddInMemoryModelRouter(routes);

var app = builder.Build();

// 4. Map the OpenAI endpoints
app.MapOpenAiCompat();

app.Run();
```

Point any OpenAI-compatible client at `http://localhost:5000`:

```python
from openai import OpenAI

client = OpenAI(base_url="http://localhost:5000/v1", api_key="any-value")
resp = client.chat.completions.create(
    model="gpt-4o",
    messages=[{"role": "user", "content": "Hello"}],
)
print(resp.choices[0].message.content)
```

## Streaming

Set `"stream": true` in the request body. The endpoint responds with `Content-Type: text/event-stream` and emits OpenAI-format SSE chunks (`data: {...}` lines) followed by `data: [DONE]`. The `finish_reason` appears on the final empty-delta chunk before `[DONE]`.

```python
stream = client.chat.completions.create(
    model="gpt-4o",
    messages=[{"role": "user", "content": "Count to five."}],
    stream=True,
)
for chunk in stream:
    print(chunk.choices[0].delta.content or "", end="", flush=True)
```

## Authentication

Implement `IInboundIdentityResolver` to validate the `Authorization: Bearer <token>` header and populate `AgentContext`:

```csharp
public sealed class JwtIdentityResolver : IInboundIdentityResolver
{
    public Task<AgentContext?> ResolveAsync(string? bearerToken, CancellationToken ct)
    {
        if (bearerToken is null) return Task.FromResult<AgentContext?>(null);
        var claims = ValidateJwt(bearerToken); // your validation logic
        return Task.FromResult<AgentContext?>(new AgentContext
        {
            UserId = claims.Subject,
            WorkspaceId = claims["workspace_id"],
        });
    }
}
```

Register it instead of (or in addition to) `AddPassThroughIdentityResolver`:

```csharp
builder.Services.AddSingleton<IInboundIdentityResolver, JwtIdentityResolver>();
```

When `ResolveAsync` returns `null`, the endpoint responds `401 Unauthorized`. The resolved `AgentContext` is pushed as the ambient context for the duration of the request — middleware such as `LlmPrometheusMiddleware` reads the `WorkspaceId` label from it automatically.

## Custom model routing

`InMemoryModelRouter` serves a static dictionary. For dynamic routing (e.g. per-workspace model assignments stored in a database), implement `IModelRouter`:

```csharp
public sealed class DbModelRouter : IModelRouter
{
    private readonly IModelRouteRepository _repo;
    private readonly ICompletionProviderFactory _factory;

    public DbModelRouter(IModelRouteRepository repo, ICompletionProviderFactory factory)
        => (_repo, _factory) = (repo, factory);

    public async Task<ModelRoute> ResolveAsync(string modelId, AgentContext context, CancellationToken ct)
    {
        var spec = await _repo.FindAsync(modelId, context.WorkspaceId, ct)
            ?? throw new ModelNotFoundException(modelId);
        return new ModelRoute(_factory.Create(spec), spec);
    }

    public Task<IReadOnlyList<ModelSpec>> ListModelsAsync(AgentContext context, CancellationToken ct)
        => _repo.ListAsync(context.WorkspaceId, ct);
}
```

```csharp
builder.Services.AddSingleton<IModelRouter, DbModelRouter>();
```

## Adding gateway middleware

Chain any `LlmGatewayMiddleware` between the HTTP layer and the provider by registering it before `AddOpenAiCompatGateway()`:

```csharp
// Rate limiting
builder.Services.AddLlmRateLimitMiddleware(new RateLimitOptions
{
    MaxRequestsPerWindow = 60,
    MaxTokensPerWindow = 500_000,
    Window = TimeSpan.FromMinutes(1),
});

// Prometheus metrics
builder.Services.AddLlmPrometheusMiddleware();

// Semantic cache
builder.Services.AddLlmSemanticCacheMiddleware();

builder.Services.AddOpenAiCompatGateway();
```

The endpoint uses `LlmGatewayPipeline` to drive the middleware chain; all middleware registered as `LlmGatewayMiddleware` singletons is applied in registration order (first registered = outermost).

## Error mapping

| Condition | HTTP status | Body |
|---|---|---|
| `IInboundIdentityResolver` returns `null` | `401 Unauthorized` | `{"error":{"type":"authentication_error","message":"..."}}` |
| `ModelNotFoundException` | `404 Not Found` | `{"error":{"type":"invalid_request_error","message":"..."}}` |
| Missing `model` field | `422 Unprocessable Entity` | `{"error":{"type":"invalid_request_error","message":"..."}}` |
| `AgentBudgetExceededException` | `429 Too Many Requests` | `{"error":{"type":"rate_limit_error","message":"..."}}` |
| Other provider exception | `500 Internal Server Error` | `{"error":{"type":"api_error","message":"..."}}` |

## See also

- [Reference: packages](../reference/packages.md) — `Vais.Agents.Gateways.OpenAiCompat` and `Vais.Agents.Gateways.Prometheus` entries.
- [Plug in gateway middleware](plug-in-gateway-middleware.md) — middleware composition guide covering `LlmPrometheusMiddleware` and others.
- [Deploy OTel and Langfuse](deploy-otel-and-langfuse.md) — pair `LlmOtelMiddleware` with an OTel collector for tracing alongside Prometheus metrics.
