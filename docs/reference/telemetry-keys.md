# Reference: telemetry keys

Complete catalogue of OpenTelemetry tag names + metric names emitted by Vais.Agents, plus the `langfuse.*` mappings from the Langfuse enricher.

## ActivitySource + Meter

| | Name |
|---|---|
| `ActivitySource` | `"Vais.Agents"` (constant: `AgenticDiagnostics.ActivitySourceName`) |
| `Meter` | `"Vais.Agents"` (constant: `AgenticDiagnostics.MeterName`) |

Shared name — one source for traces, one meter for metrics, same string.

## Activity — `chat` span

One Activity per `AskAsync` / `StreamAsync` run. Name: `"chat"` at start; renamed to `"chat {model}"` once the response provides a model id. Kind: `Client`.

### GenAI semantic-convention tags (set by `StatefulAiAgent`)

| Tag | Source |
|---|---|
| `gen_ai.system` | `ICompletionProvider.ProviderName` (`"SemanticKernel"`, `"MicrosoftAgentFramework"`) |
| `gen_ai.operation.name` | always `"chat"` |
| `gen_ai.response.model` | `CompletionResponse.ModelId` |
| `gen_ai.usage.input_tokens` | `CompletionResponse.PromptTokens` (summed across turns) |
| `gen_ai.usage.output_tokens` | `CompletionResponse.CompletionTokens` (summed across turns) |
| `error.type` | exception type name, on failure |

### `vais.*` extension tags (VAIS-specific)

| Tag | Source | Constant |
|---|---|---|
| `vais.agent.name` | `AgentContext.AgentName` / `StatefulAgentOptions.AgentName` | `AgenticTags.AgentName` |
| `vais.user.id` | `AgentContext.UserId` | `AgenticTags.UserId` |
| `vais.tenant.id` | `AgentContext.TenantId` | `AgenticTags.TenantId` |
| `vais.correlation.id` | `AgentContext.CorrelationId` | `AgenticTags.CorrelationId` |

## Activity status

| Outcome | `Activity.Status` | Additional |
|---|---|---|
| Run completed | `ActivityStatusCode.Ok` | — |
| Run failed | `ActivityStatusCode.Error` | `error.type` tag set to short exception type name |
| Run cancelled (OperationCanceledException) | `ActivityStatusCode.Error` | `error.type = "OperationCanceledException"` — but note: **no usage record is emitted** on cancellation |

## Metrics (from `OpenTelemetryUsageSink`)

| Metric | Instrument | Unit | Dimensions |
|---|---|---|---|
| `gen_ai.client.token.usage` | `Histogram<long>` | `{token}` | `gen_ai.system`, `gen_ai.response.model`, `gen_ai.token.type = "input" \| "output"` |
| `gen_ai.client.operation.duration` | `Histogram<double>` | `s` | `gen_ai.system`, `gen_ai.response.model`, `gen_ai.operation.name`, `error.type` (on failure) |

Token histogram splits into two recordings per call — one for input, one for output. Duration records once.

## Langfuse enricher (`LangfuseEnrichmentFilter`)

Adds to the active Activity on every `AskAsync` turn (not applied on `StreamAsync` — known gap):

| Tag | Source |
|---|---|
| `langfuse.user.id` | `AgentContext.UserId` (or `AnonymousUserFallback` from options) |
| `langfuse.session.id` | `AgentContext.CorrelationId` (configurable) |
| `langfuse.trace.name` | `AgentContext.AgentName` / `options.AgentName` |
| `langfuse.tags` | `LangfuseEnrichmentOptions.DefaultTags` (array, joined) |
| `langfuse.trace.metadata.{key}` | one tag per entry in `LangfuseEnrichmentOptions.Metadata` |

Langfuse's OTel collector pipeline recognises these tag names natively — no Langfuse-specific SDK required.

## `UsageRecord` fields (→ `IUsageSink`)

Per turn, after the provider returns (or after failure):

| Field | Description |
|---|---|
| `ProviderName` | `ICompletionProvider.ProviderName` |
| `ModelId` | `CompletionResponse.ModelId` (`"unknown"` if provider didn't supply) |
| `PromptTokens` | nullable |
| `CompletionTokens` | nullable |
| `Duration` | wall-clock from turn start to emit |
| `StartedAt` | UTC timestamp of turn start |
| `Succeeded` | `false` when any exception reached run exit |
| `AgentName` | context or options |
| `UserId` | context |
| `TenantId` | context |
| `CorrelationId` | context |
| `ErrorType` | exception type name, on failure |

`OpenTelemetryUsageSink` translates `UsageRecord` → the two histogram metrics above. Other sinks (DB, HTTP, Kafka) consume the full record as they see fit.

## Constants in code

All `vais.*` names are available as `const string` in `Vais.Agents.Core.AgenticTags`:

```csharp
using Vais.Agents.Core;

// Don't type strings — use:
activity.SetTag(AgenticTags.UserId, ctx.UserId);
```

`gen_ai.*` names are the spec; they aren't re-exported as constants (OpenTelemetry's own `SemanticConventions` package carries them).

## See also

- [Observability concept](../concepts/observability.md)
- [ADR 0002 — OTel GenAI conventions](../adr/0002-otel-genai-conventions.md)
- [Deploy OTel and Langfuse guide](../guides/deploy-otel-and-langfuse.md)
