# Guide: wire context sections

See, per turn, **what fraction of the LLM context window each producer consumed** — persona text, tenant policy, retrieved RAG chunks, anything else you wire in. The same per-section breakdown lands in the structured log, in Langfuse's metadata panel, in Prometheus metrics, and in a shipped Grafana dashboard. When the packer drops a section because the budget is tight, you see exactly which one and why.

This guide takes an agent you already ship and adds three named producers, then walks the three observability surfaces and a deliberate over-budget run that triggers a drop.

## Prerequisites

- One declarative or in-process agent that already answers a question (the [hello-agent](../README.md#hello-agent) sample or your own).
- The local-dev stack running with the `-Metrics` overlay so Prometheus + Grafana are reachable:

  ```powershell
  cd local-dev
  .\dev.ps1 start -Metrics
  ```

  Langfuse, Prometheus, and Grafana are then on:
  - Langfuse: <http://localhost:3000>
  - Prometheus: <http://localhost:9090>
  - Grafana: <http://localhost:3003> (anonymous viewer enabled)

- An LLM API key wired into your agent (the existing path your hello agent already uses).

## Packages

```xml
<PackageReference Include="Vais.Agents.Core" Version="0.4.0-preview" />
<PackageReference Include="Vais.Agents.Observability.OpenTelemetry" Version="0.4.0-preview" />
<PackageReference Include="Vais.Agents.Observability.Langfuse" Version="0.4.0-preview" />
<PackageReference Include="Vais.Agents.Observability.Prometheus" Version="0.4.0-preview" />
<!-- Optional, for the retrieval producer in step 2: -->
<PackageReference Include="Vais.Agents.Persistence.VectorData" Version="0.4.0-preview" />
```

The three observability packages are independently opt-in — drop the Langfuse one if you're not running Langfuse, drop Prometheus if you're not scraping. The Core sink (`LoggingSectionSink`) is always available; no extra package required.

## Step 1 — what you'll build

By the end you'll have an agent that, per turn, emits a `SectionsBuilt` `Information` log entry. `LoggingSectionSink` writes the entry on one line — pretty-printed below for readability:

```
SectionsBuilt agent=research-helper run=4b… turn=1
  sections.count=3 budget.used=0.31 budget.dropped=0 budget.truncated=0
  sections=[
    {"id":"system.persona", "kind":"SystemSegment", "producer":"PersonaContributor",
     "chars":48, "ratio":0.04, "outcome":"included"},
    {"id":"system.policy",  "kind":"SystemSegment", "producer":"TenantPolicyContributor",
     "chars":74, "ratio":0.07, "outcome":"included"},
    {"id":"retrieval.docs", "kind":"SystemSegment", "producer":"KnowledgeRetrievalContextProvider",
     "chars":1042, "ratio":0.89, "outcome":"included"}
  ]
```

— and the same breakdown shows up in Langfuse trace metadata and on a stacked-area Grafana panel keyed by `section_id`. In step 6 you'll watch the packer drop `retrieval.docs` after you tighten the budget.

## Step 2 — register section producers

Add three producers to your agent. Two are `ISystemPromptContributor` (composed by the shipped `AggregatingSystemPromptComposer` into one section per contributor); the third is `KnowledgeRetrievalContextProvider`, which already emits a `retrieval.docs` section out of the box.

```csharp
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Persistence.VectorData;

sealed class PersonaContributor : ISystemPromptContributor
{
    public int Priority => 0;                                // runs first
    public string SectionId => "system.persona";             // becomes the Section.Id

    public ValueTask<string?> ContributeAsync(AgentContext ctx, CancellationToken ct = default)
        => ValueTask.FromResult<string?>("You are a careful research assistant. Cite sources.");
}

sealed class TenantPolicyContributor(string tenantId, string policy) : ISystemPromptContributor
{
    public int Priority => 10;                               // runs after persona
    public string SectionId => "system.policy";

    public ValueTask<string?> ContributeAsync(AgentContext ctx, CancellationToken ct = default)
        => ctx.TenantId == tenantId
            ? ValueTask.FromResult<string?>($"Tenant policy ({tenantId}): {policy}")
            : ValueTask.FromResult<string?>(null);
}
```

Wire them — plus an existing `IKnowledgeRetriever` over your vector store. We'll build the composer + retrieval provider now and pass them into `StatefulAgentOptions` once at the end of this guide, after every observability sink is also registered. (`StatefulAgentOptions` is a class with `init` setters, so all fields are set in one object-initializer block — there's no `with`-style post-construction edit.)

```csharp
var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
{
    new PersonaContributor(),
    new TenantPolicyContributor(tenantId: "acme", policy: "Never reveal pricing; route to sales@example.com."),
});

var retrieval = new KnowledgeRetrievalContextProvider(
    retriever,                                               // your IKnowledgeRetriever
    new KnowledgeRetrievalOptions { TopK = 5 });
```

What's running per turn:

1. The composer emits `system.persona` (Order=0) and `system.policy` (Order=10) as two `SystemSegment` sections, each tagged with the contributor's type name as `ProducerId`.
2. `KnowledgeRetrievalContextProvider` queries the vector store and emits one `retrieval.docs` section (`Budget.Priority = 5` so it can be shed under pressure).
3. `DefaultSectionResolver` validates ids, enforces ordering, rejects collisions.
4. `DefaultSectionWindowPacker` runs against `SectionBudgetContext.Unlimited` (the default) — identity behaviour for now.
5. The flattener collapses the section list into the same `CompletionRequest` the model sees today.

The wire format is unchanged. Only the per-turn telemetry surface is new.

## Step 3 — verify in the structured log

Register the logging sink in your DI container:

```csharp
services.AddSingleton<ISectionTelemetrySink, LoggingSectionSink>();
```

(`LoggingSectionSink` lives in `Vais.Agents.Core`. It pulls `ILogger<LoggingSectionSink>` from DI — anything that writes `Information`-level entries works: Serilog, NLog, the .NET console provider.)

We'll resolve the sink list out of the service provider and hand it to `StatefulAgentOptions.SectionTelemetrySinks` at the end of step 6. For now, the sink is registered — every other sink in steps 4 and 5 piggybacks on the same `ISectionTelemetrySink` collection.

Run one turn (after step 6 wires the agent) and grep the log for `SectionsBuilt`. You should see a single `Information` entry per turn with the shape shown in step 1: top-level scalar fields (`agent`, `run`, `turn`, `sections.count`, `budget.used`, `budget.dropped`, `budget.truncated`) plus the per-section JSON blob. The JSON is one line — log shippers like Loki / Elasticsearch parse it as a nested structure.

Quick check: the sum of `chars` across `included` sections equals `budget.used * target` (or just the total chars when no budget is set). If retrieval is the largest by ratio, you're looking at a RAG-dominated turn.

## Step 4 — see it in Langfuse

The Langfuse section sink decorates the **per-turn span** (the same span the existing `gen_ai.*` tags ride on) with `langfuse.section.<id_normalised>.*` tags plus a JSON `section_breakdown` blob. Dots in section ids become underscores at the Langfuse boundary so the filter UI stays usable — `retrieval.docs` becomes `retrieval_docs`.

Wire it:

```csharp
using Vais.Agents.Observability.Langfuse;

services.AddLangfuseSectionEnrichment();
```

This package depends on the standard Vais OTel wiring — if you haven't already added `services.AddAgenticOpenTelemetrySink()` plus a tracer that exports to Langfuse's OTLP endpoint, follow [deploy-otel-and-langfuse.md](deploy-otel-and-langfuse.md) first; the section sink rides on the same exporter.

Run another turn, open <http://localhost:3000>, find the generation, expand **Trace metadata**. You should see:

```
section_breakdown        {"system_persona":0.04,"system_policy":0.07,"retrieval_docs":0.89}
section_system_persona_chars   48
section_system_persona_ratio   0.04
section_system_policy_chars    74
section_retrieval_docs_chars   1042
section_retrieval_docs_ratio   0.89
…
```

You can now filter Langfuse generations in the UI by, e.g., `section.retrieval_docs.ratio > 0.8` — every turn where retrieval ate more than 80% of the window. The `section_breakdown` blob is also queryable as a single field in Langfuse's metadata panel.

## Step 5 — see it in Grafana

Wire the Prometheus sink + the OTel sink (the OTel sink also feeds Langfuse if you skipped step 4):

```csharp
using Vais.Agents.Observability.Prometheus;
using Vais.Agents.Observability.OpenTelemetry;

services.AddAgenticOpenTelemetrySectionSink();
services.AddAgenticPrometheusSectionSink();
```

The Prometheus sink writes six time series into the default `Metrics.DefaultRegistry`. Expose them by calling `app.MapPrometheusScrapingEndpoint()` on your `WebApplication` (the same call the local-dev runtime uses) — `app.UseHttpMetrics() + app.MapMetrics()` works too if you want HTTP request metrics alongside the section metrics. Local-dev's `prometheus.yml` scrapes `vais-runtime:8080/metrics` every 15s — keep that target shape.

Metrics:

| Metric | Type | Key labels |
|---|---|---|
| `vais_request_section_chars` | histogram | `section_id, kind, producer, agent_id` |
| `vais_request_section_tokens` | histogram | same — only when a tokenizer is wired |
| `vais_request_section_ratio` | histogram | `section_id, agent_id` |
| `vais_request_section_outcome_total` | counter | `section_id, outcome` |
| `vais_request_budget_used_ratio` | histogram | `agent_id` |
| `vais_request_sections_per_turn` | histogram | `agent_id` |

Import the shipped dashboard. From the repo root:

```powershell
cp agentic/deploy/observability/grafana/dashboards/context-sections.json `
   local-dev/grafana/provisioning/dashboards/

# Re-apply the metrics overlay so Grafana picks up the new file.
cd local-dev
.\dev.ps1 start -Metrics
```

Open Grafana (<http://localhost:3003>) → **Dashboards → Vais — Context Sections**. Five panels:

1. **Context Composition Over Time** — stacked area of `vais_request_section_chars_sum` rate, split by `section_id`. After ~10 turns you see the mix at a glance: persona small, policy small, retrieval dominant.
2. **Average Section Ratio by Agent** — heat-coloured table; cells above 0.5 highlight a producer eating half the window for that agent.
3. **Budget Pressure** gauge — green below 0.7, yellow up to 0.9, red above. Stays at 0 until you set a budget (step 6).
4. **Drop / Truncate Events** — non-zero only when the packer is shedding load. Watch this in step 6.
5. **Sections per Turn** heatmap — distribution shifts here are usually a manifest change you didn't expect.

If the dashboard imports but panels show **No data**, give the agent a few more turns so Prometheus has scrape data; the histograms need at least one scrape interval to produce rates.

## Step 6 — trigger a packer drop

Now construct the agent. Resolve the sink list from DI, set a tight character budget (`MaxChars: 256` — comfortably below the typical retrieval payload of 1 KB+ for `TopK = 5`), and pass everything to `StatefulAgentOptions` in one block:

```csharp
var serviceProvider = services.BuildServiceProvider();

var agent = new StatefulAiAgent(
    completionProvider,
    new StatefulAgentOptions
    {
        AgentName = "research-helper",
        SystemPromptComposer = composer,
        ContextProviders = new IContextProvider[] { retrieval },
        SectionTelemetrySinks = serviceProvider
            .GetServices<ISectionTelemetrySink>()
            .ToArray(),
        SectionBudget = new SectionBudgetContext(MaxChars: 256),
        // SectionResolver, SectionWindowPacker all default — no setup needed.
    });
```

The packer's shedding rule when the total exceeds the cap: drop by descending `Section.Budget.Priority` (priority 10 first, priority 0 critical and never dropped); within a priority tier, drop the largest section first; sections without a `Budget` are treated as priority 5. With the wiring above all three sections sit at priority 5 (`AggregatingSystemPromptComposer` doesn't currently set `Budget` on composer-emitted sections; `KnowledgeRetrievalContextProvider` sets it to `Priority: 5` explicitly), so the size tiebreak decides — and `retrieval.docs` (1 KB+) is by far the largest. It drops first.

Run one more turn and check all three surfaces:

- **Log** — the `retrieval.docs` measurement now reads `"outcome":"dropped"` with a non-zero `dropped_chars`; `budget.dropped=1`; `budget.used` settles around the persona+policy total over the cap (e.g. ~0.48 for 122 chars used out of a 256-char budget).
- **Langfuse** — the `section_breakdown` blob no longer lists `retrieval_docs`; the budget-used ratio rises.
- **Grafana** — the **Drop / Truncate Events** panel spikes on `retrieval.docs (dropped)`; the **Budget Pressure** gauge climbs.

The packer made a decision the operator can see, attribute to a producer, and act on. That's the payoff: every section is named, every drop has a paper trail.

Reset the budget by removing the `SectionBudget` field (the default is `SectionBudgetContext.Unlimited`) once you're done.

## Optional — write a custom producer

A 25-line `IContextProvider` that emits a `metadata.user_preferences` section:

```csharp
sealed class UserPrefsContextProvider(IUserPreferencesStore store) : IContextProvider
{
    public async ValueTask<ContextContribution> InvokeAsync(
        ContextInvocationContext context, CancellationToken ct = default)
    {
        if (context.AmbientContext.UserId is not string userId)
            return ContextContribution.Empty;

        var prefs = await store.GetAsync(userId, ct);
        if (prefs is null) return ContextContribution.Empty;

        var section = new Section(
            Id: "metadata.user_preferences",
            Kind: SectionKind.Metadata,                       // observability-only; never flattens
            Payload: new MetadataPayload(prefs.ToDictionary()),
            ProducerId: nameof(UserPrefsContextProvider));

        return new ContextContribution(new[] { section });
    }
}
```

Register it via `StatefulAgentOptions.ContextProviders` and it appears in every snapshot. `Metadata` payloads never reach the model, but they ride the same observability rails — useful for "this turn loaded N preference rows" without spending tokens.

## Things that catch people

- **`SystemPromptComposer` overrides `SystemPrompt`.** When both are set, the composer wins — the plain `SystemPrompt` string is ignored and contributors decide the persona. If your persona doesn't appear in the log, double-check you set `SystemPromptComposer`, not `SystemPrompt`.
- **Section id collisions throw.** Two providers emitting the same `Section.Id` raise `SectionCollisionException` at resolve time — by design. Namespace your ids (`memory.user.short` vs `memory.user.long`) instead of letting the resolver merge them silently.
- **Sinks are registration-order.** Sink failures are logged at `Warning` and swallowed (per architecture principle P9: telemetry never breaks the data path), but order matters when sinks share state — register cheap synchronous sinks before async exporters.
- **Langfuse uses underscores.** Section id `retrieval.docs` lands in Langfuse as `retrieval_docs`. Filter expressions use the normalised form.
- **Prometheus cardinality.** `section_id` is bounded by your registered producers (typically 5–10) and `agent_id` is the operator's responsibility — same constraint as the existing `vais_agent_runs_total`. Avoid emitting unique-per-turn ids (e.g. `retrieval.docs.<run-guid>`) or you'll blow up the time-series database.
- **Token counts are opt-in.** All histograms run in character mode by default. Wire an `ITokenCounter` into `SectionBudgetContext` to switch to tokens; the `*_tokens` metric only emits when a counter is present.

## See also

- [Context concept](../concepts/context.md) — the resolver / packer / flattener pipeline.
- [Prompt concept](../concepts/prompt.md) — composer + contributor model and `SectionId`.
- [Events reference](../reference/events.md) — `RequestSectionsBuilt` for programmatic subscribers.
- [Other extension seams](../extensions/other-extension-seams.md) — `ISectionWindowPacker` for custom budgeting.
- [RAG via VectorData](wire-rag-via-vectordata.md) — the `KnowledgeRetrievalContextProvider` used in step 2.
- [Deploy OTel + Langfuse](deploy-otel-and-langfuse.md) — the OTel exporter the Langfuse sink rides on.
