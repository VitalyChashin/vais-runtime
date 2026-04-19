# Prompt

The system prompt — what you tell the model about its role, persona, constraints, and output format — is composed, not just a constant. Vais.Agents exposes three contracts:

- **`IPromptTemplate`** — a render-with-variables service (neutral; consumer-controlled use).
- **`ISystemPromptComposer`** — the per-turn composer that produces the base system prompt.
- **`ISystemPromptContributor`** — ordered contributors an aggregating composer combines.

## Why composition matters

Baseline system prompts are never static in production: they weave a persona string + current-user metadata + conditional policy fragments + active-tool summaries + a couple of "important: never …" lines. Hard-coding all of that into `StatefulAgentOptions.SystemPrompt` puts a string-concat monstrosity in consumer land. Composer + contributors decomposes it.

## Core types

```csharp
namespace Vais.Agents;

public interface IPromptTemplate
{
    Task<string> RenderAsync(string template, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default);
}

public interface ISystemPromptComposer
{
    Task<string?> ComposeAsync(AgentContext context, CancellationToken cancellationToken = default);
}

public interface ISystemPromptContributor
{
    int Priority { get; }
    Task<string?> ContributeAsync(AgentContext context, CancellationToken cancellationToken = default);
}
```

**`IPromptTemplate`** is deliberately NOT on `StatefulAgentOptions` — `StatefulAiAgent` doesn't consume it directly. Consumers inject it into their own composer / contributors for reuse. Keeping it off the agent avoids locking the pipeline into one template dialect.

## Default implementations

- **`FormatStringPromptTemplate.Instance`** — simple `{key}` substitution. Unknown keys pass through as literal text. Null values render as empty strings. Non-string values call `.ToString()`. Unmatched `{` / `}` braces emit verbatim.
- **`AggregatingSystemPromptComposer(IEnumerable<ISystemPromptContributor>)`** — orders contributors by `Priority` ascending (lower runs first), awaits each `ContributeAsync`, joins non-null/non-empty results with `\n\n`, returns `null` when nothing contributes.

## Wiring

```csharp
var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
{
    new PersonaContributor("You are a helpful customer-support assistant."),
    new TenantPolicyContributor(policyStore),  // priority 10
    new AvailableToolsContributor(registry),   // priority 20
});

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        SystemPromptComposer = composer,
    });
```

The composer replaces `StatefulAgentOptions.SystemPrompt` entirely when both are set — composer wins. (Decision option (a) from the v0.4 prompt pillar — avoids merge-order ambiguity between "static string" and "dynamic composer".)

Context providers' `SystemPromptAddendum` contributions still concatenate on top of the composer's result — canonical shape for a RAG-plus-persona prompt is `{composed-base}\n\n{retrieved-context}`.

## A custom contributor

```csharp
sealed class PersonaContributor(string persona) : ISystemPromptContributor
{
    public int Priority => 0;  // runs first
    public Task<string?> ContributeAsync(AgentContext ctx, CancellationToken ct)
        => Task.FromResult<string?>(persona);
}

sealed class TenantPolicyContributor(ITenantPolicyStore store) : ISystemPromptContributor
{
    public int Priority => 10;
    public async Task<string?> ContributeAsync(AgentContext ctx, CancellationToken ct)
    {
        if (ctx.TenantId is null) return null;
        var policy = await store.GetPolicyAsync(ctx.TenantId, ct);
        return policy is null ? null : $"Tenant policy: {policy}";
    }
}
```

Priority is ascending — lower numbers run first. Null / empty returns are skipped. The final composer output is `"You are a helpful customer-support assistant.\n\nTenant policy: …\n\n…"`.

## Using `IPromptTemplate` inside a contributor

```csharp
sealed class GreetingContributor(Vais.Agents.IPromptTemplate template) : ISystemPromptContributor
{
    public int Priority => 5;
    public Task<string?> ContributeAsync(AgentContext ctx, CancellationToken ct)
        => template.RenderAsync(
            "You are assisting {user}. Today is {date}.",
            new Dictionary<string, object?>
            {
                ["user"] = ctx.UserId ?? "a visitor",
                ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            },
            ct);
}
```

Use `Vais.Agents.IPromptTemplate` (fully qualified) if your consumer file also imports `Microsoft.SemanticKernel` — SK ships an identically-named interface in its namespace.

## Extension points

- **Bring your own `ISystemPromptComposer`** — if the aggregating pattern doesn't fit, implement the single `ComposeAsync` method.
- **Bring your own `ISystemPromptContributor`s** — any number, any priority, any async.
- **Bring your own `IPromptTemplate`** — the default `FormatStringPromptTemplate` is intentionally minimal; consumers needing handlebars / liquid / razor implement their own.

## Observability

No per-contributor events in v0.4. The composed result lives in `CompletionRequest.SystemPrompt` at the provider boundary — visible to `IAgentFilter`s and to the `gen_ai.*` activity tags (the prompt itself isn't tagged — only model-side metadata is).

## Limitations / known gaps

- **No built-in caching** of composed prompts — every turn recomposes. If a contributor is expensive (DB fetch, HTTP call), cache inside the contributor.
- **No "required" / "forbidden" combinators** — if two contributors disagree (one says "never discuss weather", another tries to discuss weather), the composer just concatenates both strings. Resolve via priority + disciplined authoring; guardrails are a separate pillar for that.
- **`IPromptTemplate` collision** with `Microsoft.SemanticKernel.IPromptTemplate` — same-named type in SK. Fully qualify or alias when both are in scope.

## See also

- [Architecture](architecture.md)
- [Context](context.md) — `SystemPromptAddendum` from providers concatenates after the composer output.
- [Execution loop](execution-loop.md) — when the composer runs per turn.
