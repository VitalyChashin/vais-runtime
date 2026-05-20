# Context

"Context" here means the information a turn needs that **isn't** in the session history — retrieved documents, ambient policy rules, conditionally-attached tools, observability-only metadata. Vais.Agents exposes context as a **provider chain** producing typed **`Section[]`**, processed through a **resolver**, **window packer**, and **telemetry emitter** before the **flattener** collapses the surviving sections into a `CompletionRequest`. All five stages run between the history reducer and the filter chain inside the agent's turn loop.

## Where context fits in the turn

```mermaid
flowchart LR
    U[User turn appended to session] --> H[History reducer]
    H --> CP[IContextProvider chain → Section[]]
    CP --> SR[ISectionResolver]
    SR --> SP[ISectionWindowPacker]
    SP --> ST[SectionTelemetryEmitter]
    ST --> FL[CompletionRequestFlattener]
    FL --> G[Input guardrails]
    G --> F[Agent filter chain]
    F --> R[Provider CompleteAsync]
```

Every `AskAsync` and `StreamAsync` turn hits the context stage. Providers run in configured order, each returning a `ContextContribution` carrying a typed `Section[]`. The resolver enforces id uniqueness and canonical ordering, the packer drops sections that don't fit the budget, telemetry fans out per-turn, and the flattener collapses sections into a `CompletionRequest` for the provider.

## Core types

```csharp
namespace Vais.Agents;

public interface IContextProvider
{
    ValueTask<ContextContribution> InvokeAsync(ContextInvocationContext context, CancellationToken cancellationToken = default);
}

public sealed record ContextContribution(IReadOnlyList<Section> Sections)
{
    public static ContextContribution Empty { get; }
}

public sealed record Section(
    string Id,                          // hierarchical, e.g. "retrieval.docs"
    SectionKind Kind,                   // SystemSegment | UserMessage | AssistantMessage | ToolMessage | ToolDeclaration | ResponseFormat | Metadata
    SectionPayload Payload,             // typed per Kind — see Section.cs
    int? Order = null,                  // sort key within Kind (explicit first, ascending; null clusters at end in registration order)
    string? ProducerId = null,          // attribution for telemetry
    SectionBudget? Budget = null);      // optional priority + per-section MaxChars

public interface ISectionResolver
{
    ValueTask<IReadOnlyList<Section>> ResolveAsync(IReadOnlyList<Section> contributed, CancellationToken ct = default);
}

public interface ISectionWindowPacker
{
    ValueTask<SectionPackResult> PackAsync(IReadOnlyList<Section> sections, SectionBudgetContext budget, CancellationToken ct = default);
}

public sealed record ContextInvocationContext(
    CompletionRequest Candidate,
    AgentContext AmbientContext,
    IAgentSession Session);
```

Providers **never mutate** — they return a `ContextContribution` whose `Sections` collection joins the resolver's input.

## Merge rules

The resolver and flattener compose sections; there's no string-level merge:

- **Id uniqueness.** Two sections with the same `Section.Id` raise `SectionCollisionException` — namespace producers (`memory.user.short` vs `memory.user.long`) rather than letting the resolver merge silently.
- **Canonical kind order.** `SystemSegment` first; then `UserMessage` / `AssistantMessage` / `ToolMessage` as one group interleaved by `Order`; then `ToolDeclaration`; then `ResponseFormat` (at most one per turn); then `Metadata`.
- **Within-kind order.** Explicit `Section.Order` ascending. Null-Order sections cluster at the end in registration order.
- **Flattener output.** `SystemSegment` payloads concatenate by `"\n\n"` into `CompletionRequest.SystemPrompt`; turn-shaped payloads collect into `History` in resolved order; tool sections feed `Tools` (last duplicate wins, warned via log); the single `ResponseFormat` payload populates `ResponseFormat`; `Metadata` sections never reach the wire.

If a provider returns `ContextContribution.Empty` (an empty section list), it's a no-op.

### Legacy three-slot mode

The pre-v0.5 `ContextContribution(string? SystemPromptAddendum, IReadOnlyList<ChatTurn>? InjectedHistory, IReadOnlyList<ITool>? AdditionalTools)` constructor still works. It emits Guid-suffixed sections (`system.legacy_addendum.<guid>`, `history.legacy_injected.<guid>.<i>`, `tools.legacy_additional.<guid>`) so multiple legacy providers in the same turn never collide. The three legacy view properties are auto-derived from the section list so existing readers (filters that read `Candidate.SystemPrompt`) see the v0.4-shape string. The legacy ctor is slated for `[Obsolete(DiagnosticId="VAIS0010")]` once all in-repo consumers migrate (SC-7); the attribute application is deferred and no removal date is set today.

## Section window packer

Runs **after** the resolver, before telemetry. Default is `DefaultSectionWindowPacker.Instance` — identity when the budget (`SectionBudgetContext.Unlimited`) imposes no caps. Under budget pressure the packer sheds sections greedy-by-priority (descending `Budget.Priority` first, size as tiebreak); priority 0 is critical and never dropped; null-`Budget` sections are treated as priority 5. Truncation applies only to `SystemSegment` and `Metadata` kinds — turn-shaped sections aren't internally truncated.

A legacy `IContextWindowPacker` on `StatefulAgentOptions.ContextWindowPacker` is automatically wrapped in `LegacyPackerAdapter` so existing custom packers keep working through one release of co-existence.

## Wiring

```csharp
var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        ContextProviders = new IContextProvider[]
        {
            new TimeAndTenantProvider(),
            new KnowledgeRetrievalContextProvider(retriever),  // emits a "retrieval.docs" Section
        },
        // SectionWindowPacker = new MyBudgetPacker(),
        // SectionBudget = new SectionBudgetContext(MaxChars: 8_000),
    });
```

Providers run top-to-bottom; later providers see contributions from earlier ones via the candidate request.

## A custom provider

```csharp
sealed class TimeAndTenantProvider : IContextProvider
{
    public ValueTask<ContextContribution> InvokeAsync(ContextInvocationContext ctx, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        var tenant = ctx.AmbientContext.TenantId ?? "(no-tenant)";

        var section = new Section(
            Id: "context.time_and_tenant",
            Kind: SectionKind.SystemSegment,
            Payload: new TextPayload($"Current time: {now}. Tenant: {tenant}."),
            ProducerId: nameof(TimeAndTenantProvider));

        return ValueTask.FromResult(new ContextContribution(new[] { section }));
    }
}
```

The `ContextInvocationContext.Candidate` is the request *as built so far* — useful if your provider needs to peek at the history or the tool list. `AmbientContext` is the `IAgentContextAccessor.Current` snapshot for the run. `Session` lets you introspect `History` if your retrieval logic keys off it.

## RAG-specific provider

`Vais.Agents.Persistence.VectorData` ships `KnowledgeRetrievalContextProvider` — takes a last-user-turn-keyed retrieval over any `Microsoft.Extensions.VectorData` store, returns the top-K chunks as a `SystemSegment` section (`Id = retrieval.docs`, `Budget.Priority = 5` so it sheds before persona under pressure). See the [persistence concept](persistence.md#rag) and the [RAG guide](../guides/wire-rag-via-vectordata.md).

## Extension points

- **`IContextProvider`** — implement one method; returns `ContextContribution` carrying `Section[]`.
- **`ISectionWindowPacker`** — for budget-aware section dropping / truncation. Default `DefaultSectionWindowPacker` is identity unless `SectionBudget` is set.
- **`ISectionResolver`** — for custom ordering or collision handling. Default `DefaultSectionResolver` enforces id uniqueness, the canonical kind order, and within-kind `Section.Order` ascending.
- **Order matters.** The agent runs providers in the order you listed. Within a section list, the resolver applies the canonical kind order plus the explicit `Section.Order` field — registration order only breaks ties among null-`Order` sections in the same kind.

## Failure semantics

Providers are **load-bearing** — an exception fails the turn (`TurnFailed` event + rethrow). This is a design decision, not an oversight: retrieval that silently swallows failures masks data-quality bugs. Consumers who want swallow semantics wrap their provider with a resilience-handling decorator. Similar choice to how `IAgentFilter` propagates exceptions.

## Observability

- **Per-section breakdown is first-class.** The `SectionTelemetryEmitter` runs once per turn between the packer and the flattener and fans a `SectionTelemetrySnapshot` (per-section chars / tokens / ratio / outcome + aggregate budget summary) to every registered `ISectionTelemetrySink`. Shipped sinks: `LoggingSectionSink` (structured `SectionsBuilt` Information log), `OtelSectionSink` (`vais.request.*` and `vais.request.section.<id>.*` Activity tags), `LangfuseSectionEnrichment` (`langfuse.section.*` tags + JSON `section_breakdown` metadata), `PrometheusSectionSink` (6 metrics keyed by `section_id, agent_id`), `EventBusSectionSink` (publishes the `RequestSectionsBuilt` agent event for programmatic subscribers).
- **`RequestSectionsBuilt`** carries the same `AgentContext` snapshot as other `AgentEvent`s — subscribers reach `RunId`, `AgentName`, `UserId`, `TenantId`, `WorkspaceId`, `CorrelationId` via the event's `Context` property.
- `vais.agent.name` + ambient context tags still apply to the per-turn Activity.

See the [wire-context-sections tutorial](../guides/wire-context-sections.md) for the end-to-end walkthrough.

## Limitations / known gaps

- **No provider-level caching contract.** Consumers cache inside their provider or wrap it. No cache-key convention baked in.
- **Section truncation only on text-shaped kinds.** The default packer truncates `SystemSegment` and `Metadata` payloads to fit `SectionBudget.MaxChars`; turn-shaped sections (`UserMessage` / `AssistantMessage` / `ToolMessage`) are either kept whole or dropped — never internally truncated. Bring a custom `ISectionWindowPacker` if you need finer-grained policy.
- **Legacy `KnowledgeRetrievalFilter`** (`Vais.Agents.Persistence.VectorData`) is `[Obsolete(DiagnosticId="VAIS0001")]` since v0.4 — it used the older `IAgentFilter` pipeline. New code uses `KnowledgeRetrievalContextProvider`. The obsolete filter still ships for backward compatibility (`KnowledgeRetrievalFilter.cs` is still present); no firm removal version is set.

## See also

- [Architecture](architecture.md)
- [Prompt](prompt.md) — composer + contributor sections combine with provider sections at the resolver.
- [Execution loop](execution-loop.md) — exact position of the context stage.
- [Wire context sections guide](../guides/wire-context-sections.md) — six-step tutorial wiring three producers and three observability surfaces.
- [Wire RAG via VectorData guide](../guides/wire-rag-via-vectordata.md)
- [Events reference](../reference/events.md) — `RequestSectionsBuilt` shape.
