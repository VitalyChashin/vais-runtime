# Guide: deploy OTel and Langfuse

Wire Vais.Agents' telemetry to an OpenTelemetry collector (for traces + metrics) and layer Langfuse on top (for LLM-specific UI views). Both sinks + enrichers are `null`-by-default; nothing emits unless you opt in.

## Packages

```xml
<PackageReference Include="Vais.Agents.Observability.OpenTelemetry" Version="0.4.0-preview" />
<PackageReference Include="Vais.Agents.Observability.Langfuse" Version="0.4.0-preview" />
<!-- OTel exporter of your choice, e.g.: -->
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.15.2" />
```

## OpenTelemetry — traces + metrics

```csharp
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Vais.Agents.Observability.OpenTelemetry;

services.AddSingleton(_ =>
    Sdk.CreateTracerProviderBuilder()
        .AddAgenticInstrumentation()                       // adds source "Vais.Agents"
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://otel-collector:4317"))
        .Build());

services.AddSingleton(_ =>
    Sdk.CreateMeterProviderBuilder()
        .AddAgenticInstrumentation()                       // adds meter "Vais.Agents"
        .AddOtlpExporter(o => o.Endpoint = new Uri("http://otel-collector:4317"))
        .Build());

services.AddAgenticOpenTelemetrySink();                    // registers OpenTelemetryUsageSink → IUsageSink
```

`AddAgenticOpenTelemetrySink` registers the sink as `IUsageSink` in DI. Anything resolving `IUsageSink` from the container (e.g. `StatefulAgentOptions.UsageSink = serviceProvider.GetRequiredService<IUsageSink>()`) gets it.

## What you see per turn

- **Activity** named `chat` at start, renamed to `chat {model}` once the response returns. Kind `Client`. Tags:
  - `gen_ai.system` — provider name (`SemanticKernel`, `MicrosoftAgentFramework`).
  - `gen_ai.operation.name` — always `chat`.
  - `gen_ai.response.model` — the model id the provider reports.
  - `gen_ai.usage.input_tokens` / `gen_ai.usage.output_tokens` — token counts when the provider supplies them.
  - `vais.agent.name` / `vais.user.id` / `vais.tenant.id` / `vais.correlation.id` — from `AgentContext`.
  - On failure: `ActivityStatusCode.Error`, `error.type` tag.
- **Histograms**:
  - `gen_ai.client.token.usage` (unit `{token}`), split by `gen_ai.token.type = "input" | "output"`.
  - `gen_ai.client.operation.duration` (unit `s`), with `error.type` dimension on failure.

See [telemetry keys reference](../reference/telemetry-keys.md) for the complete tag / metric catalogue.

## Cancellation is not a failure

If `AskAsync` is cancelled, **no `UsageRecord` is emitted**. Design decision — a cancelled call is not an error, and inflating error metrics with cancellations skews alerting. The Activity still closes (with `Ok` status if the cancellation happened during the final turn's normal path, `Error` only if an actual exception surfaced).

## Langfuse enrichment — on top of OTel

Langfuse reads OpenTelemetry OTLP output and maps specific `langfuse.*` tag names into its UI. Our enricher wires an `IAgentFilter` that reads `IAgentContextAccessor.Current` and adds those tags to the active Activity.

```csharp
using Vais.Agents.Observability.Langfuse;

services.AddLangfuseEnrichment(new LangfuseEnrichmentOptions
{
    DefaultTags = new[] { "vais-agents", "production" },
    AnonymousUserFallback = "anonymous",
    // Static metadata always attached:
    Metadata = new Dictionary<string, string>
    {
        ["deployment.region"] = "eu-west-1",
    },
});
```

The enricher registers as an `IAgentFilter` — it sees every `AskAsync` turn (streaming turns don't apply the filter pipeline in v0.4.1). Tags emitted on the active Activity:

- `langfuse.user.id` ← `vais.user.id`
- `langfuse.session.id` ← correlation id (configurable)
- `langfuse.trace.name` ← agent name
- `langfuse.tags` ← `DefaultTags` + per-run additions
- `langfuse.trace.metadata.*` ← static metadata

Langfuse's OTel collector pipeline recognises these and lights up the trace in Langfuse's UI — no Langfuse-specific SDK required.

## Attaching ambient context

For the `vais.*` / `langfuse.*` tags to have values, someone has to set `IAgentContextAccessor.Current`. In a web handler:

```csharp
using Vais.Agents;
using Vais.Agents.Core;

app.Use(async (httpCtx, next) =>
{
    var ambient = new AgentContext(
        UserId: httpCtx.User.Identity?.Name,
        TenantId: httpCtx.Request.Headers["X-Tenant-Id"].FirstOrDefault(),
        CorrelationId: httpCtx.TraceIdentifier);

    var accessor = (AsyncLocalAgentContextAccessor)httpCtx.RequestServices.GetRequiredService<IAgentContextAccessor>();
    using (accessor.Push(ambient))
    {
        await next();
    }
});
```

Any `StatefulAiAgent` running inside this `using` block sees the ambient context and emits the matching tags.

## Wiring into the agent

```csharp
var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        UsageSink = serviceProvider.GetRequiredService<IUsageSink>(),
        Filters = new[] { serviceProvider.GetRequiredService<LangfuseEnrichmentFilter>() },
        // ContextAccessor is resolved from AsyncLocal by default; override via options only if needed.
    });
```

## Verifying

1. Start a local OTel collector (e.g. `otel/opentelemetry-collector-contrib` in Docker).
2. Run the agent; drive a turn.
3. Inspect the collector logs — you should see a `chat {model}` span + token-usage metric.

```bash
docker run -d --name otel -p 4317:4317 -p 4318:4318 \
  -v $(pwd)/otel-config.yaml:/etc/otel/config.yaml \
  otel/opentelemetry-collector-contrib:latest \
  --config=/etc/otel/config.yaml
```

For Langfuse, route traces to Langfuse's OTLP endpoint instead (or alongside).

## Things that catch people

- **ActivitySource is process-global.** If you run agent code inside a test harness that listens on `Vais.Agents`, parallel tests can leak activities into one another. Disable xUnit parallelisation for the test assembly (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) — noted in the observability concept.
- **`OpenTelemetryUsageSink` is `IDisposable`.** Register it as a singleton in DI; its internal `Meter` disposes on container shutdown.
- **Langfuse enricher is a filter, not a sink.** It doesn't replace the usage sink; both run (sink for billing, filter for per-span enrichment).
- **Streaming turns bypass filters.** The Langfuse enricher only sees `AskAsync` turns in v0.4.1. Consumers who also need Langfuse on streaming subscribe to the event bus and emit their own `langfuse.*` tags from there.

## See also

- [Observability concept](../concepts/observability.md)
- [Telemetry keys reference](../reference/telemetry-keys.md)
- [Events reference](../reference/events.md)
- Sample: `samples/ObservabilityOtelConsole/` (per samples plan)
