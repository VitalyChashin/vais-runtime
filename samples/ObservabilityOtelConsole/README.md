# ObservabilityOtelConsole

Wires `AddAgenticInstrumentation` on both Tracer + Meter providers with console exporters, plus `AddLangfuseEnrichment`. Drives a turn through a scripted provider and prints the emitted `chat` span + `gen_ai.client.*` metrics.

**Concepts:** [observability](../../docs/concepts/observability.md).
**Reference:** [telemetry keys](../../docs/reference/telemetry-keys.md).
**Packages:** `Vais.Agents.Abstractions`, `Vais.Agents.Core`, `Vais.Agents.Observability.OpenTelemetry`, `Vais.Agents.Observability.Langfuse`, `OpenTelemetry.Exporter.Console`.
**Needs API key:** no.

```bash
dotnet run --project samples/ObservabilityOtelConsole
```

Look for `Activity.Tags[gen_ai.system], gen_ai.response.model, vais.agent.name, langfuse.user.id, …` on the emitted span.
