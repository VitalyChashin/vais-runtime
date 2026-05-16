# Agent input middleware

**Audience**: you want to intercept and reshape the inbound message before the agent receives it — without modifying the agent or the plugin code.

Agent input middleware sits between the caller (Orleans grain, graph node executor) and the `IAiAgent.AskAsync` call. It is the P12 "mandatory inbound" seam: memory injection, retrieval augmentation, HCM, DIEE, and PAS plug in here.

## When to use this seam

| Use case | Middleware? |
|---|---|
| Prepend retrieved documents to the user message | Yes |
| Rewrite the user message before it reaches the LLM | Yes |
| Intercept LLM completions | No — use `LlmGatewayMiddleware` |
| Intercept tool calls | No — use `ToolGatewayMiddleware` |
| Guard inputs/outputs for safety | No — use `InputGuardrail` / `OutputGuardrail` |

## Contract

```csharp
public abstract class AgentInputMiddleware
{
    public virtual Task InvokeAsync(
        AgentInputContext context,
        Func<Task> next,
        CancellationToken cancellationToken = default)
        => next();
}
```

`AgentInputContext` carries:

| Property | Type | Mutable? | Description |
|---|---|---|---|
| `AgentId` | `string` | No | Stable agent identifier |
| `RunId` | `string?` | No | Graph run id, or null for direct calls |
| `NodeId` | `string?` | No | Graph node id, or null for direct calls |
| `Message` | `string` | **Yes** | The inbound message — mutate to reshape |
| `Properties` | `Dictionary<string, object?>` | **Yes** | Bag for passing data between middleware layers |

Call `next()` to continue the chain; return without calling `next()` to short-circuit (suppress the turn).

## Quick example — prepend retrieved context

```csharp
public sealed class RetrievalAugmentationMiddleware(IVectorStore store) : AgentInputMiddleware
{
    public override async Task InvokeAsync(
        AgentInputContext context,
        Func<Task> next,
        CancellationToken cancellationToken = default)
    {
        var docs = await store.SearchAsync(context.Message, topK: 3, cancellationToken);
        if (docs.Count > 0)
        {
            var snippet = string.Join("\n---\n", docs.Select(d => d.Content));
            context.Message = $"[Context]\n{snippet}\n\n[User]\n{context.Message}";
        }
        await next();
    }
}
```

## Registering middleware

### Singleton (applies to every agent)

```csharp
services.AddAgentInputMiddleware<RetrievalAugmentationMiddleware>();
```

Middleware registered this way is picked up by the manifest translator and applied in registration order (outermost first).

### Named (activated per-agent from the manifest)

```csharp
services.AddNamedAgentInputMiddleware(
    "retrieval-augmentation",
    (spec, sp) => new RetrievalAugmentationMiddleware(sp.GetRequiredService<IVectorStore>()));

services.AddDefaultAgentInputMiddlewareFactory();
```

Then in the agent manifest:

```yaml
spec:
  inputMiddleware:
    - name: retrieval-augmentation
```

### Direct (via `StatefulAgentOptions`)

```csharp
var options = new StatefulAgentOptions
{
    InputMiddleware = [new RetrievalAugmentationMiddleware(store)],
};
```

## Execution order

The chain is built outermost-first: the first element in `InputMiddleware` is the outermost layer (called first, returns last). This mirrors `ToolGatewayMiddleware` and `LlmGatewayMiddleware`.

```
caller → mw[0] → mw[1] → mw[2] → agent.AskAsync
```

## Instances must be reentrant

Do not store per-call state in instance fields. Middleware instances are shared across concurrent grain activations. Use local variables inside `InvokeAsync` instead.

## P12 placement

Input middleware is the **mandatory inbound zone** of the P12 plugin sandbox contract. The runtime guarantees that every call to an agent — whether from a direct grain call or a graph node executor — passes through the registered middleware chain. Plugin code cannot bypass it.

For the outbound side (LLM calls, tool calls) see:

- [LLM gateway middleware](author-an-llm-gateway-middleware.md)
- [MCP gateway middleware](author-an-mcp-gateway-middleware.md)
