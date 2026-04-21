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

## v0.11 — HTTP idempotency middleware

`AgentControlPlaneIdempotencyMiddleware` (`Vais.Agents.Control.Http.Server`) emits per-request tags on the outer ASP.NET Core activity when an `Idempotency-Key` header is present:

| Tag | Source | Values |
|---|---|---|
| `vais.control.idempotency.key` | `Idempotency-Key` request header | opaque ≤ 255 chars |
| `vais.control.idempotency.status` | result of `IIdempotencyStore.TryBeginAsync` | `"new"`, `"replayed"`, `"in-flight"`, `"mismatch"` |
| `vais.control.idempotency.fingerprint` | SHA-256 of the request body (truncated hex) | 16-char prefix |
| `vais.control.idempotency.store` | which `IIdempotencyStore` served the call | `"InMemory"`, `"Orleans"` |

`Idempotency-Replayed: true` on the response correlates with `vais.control.idempotency.status == "replayed"`. Consumers watching `replayed` volume vs. `new` volume get a clean signal on retry rate without parsing bodies.

## v0.12 — SSE streaming-invoke

`/v1/agents/{id}/invoke/stream` emits tags on the request activity + per-event tags on a child `vais.stream` activity:

| Tag | Source | Values |
|---|---|---|
| `vais.stream.session` | `AgentInvocationRequest.SessionId` | opaque |
| `vais.stream.event-count` | events yielded before connection closed | integer, set at end |
| `vais.stream.heartbeat-count` | SSE comments emitted | integer, set at end |
| `vais.stream.closed-reason` | how the stream terminated | `"completed"`, `"failed"`, `"client-cancel"`, `"server-cancel"` |
| `vais.stream.event-kind` | event-subtype name on per-event child span | kebab-case wire name (`turn.started`, `delta`, `tool.completed`, …) |

Per-event child spans are sampled — collector-side sampling decisions apply. Disable via `StreamingInvokeOptions.EmitPerEventSpans = false` when the event volume overwhelms exporter budgets.

## v0.13 — Kubernetes operator

KubeOps 10.3.4 emits its own metrics on the operator's meter (`KubeOps.Operator`). Cross-reference with Vais-specific controller tags:

| Tag | Source | Values |
|---|---|---|
| `vais.operator.crd-version` | CRD version the controller is watching | `"vais.io/v1alpha1"` |
| `vais.operator.phase` | `AgentEntity.Status.Phase` | `Pending` / `Creating` / `Active` / `Updating` / `Error` / `Terminating` |
| `vais.operator.verb` | verb issued against the HTTP control plane | `"create"` / `"update"` / `"evict"` |
| `vais.operator.manifest-revision` | `status.manifestRevision` at reconcile time | `sha256:` hex |
| `vais.operator.observed-generation` | `status.observedGeneration` after reconcile | integer |

The operator reuses `Vais.Agents.ActivitySource` — reconcile activities appear alongside runtime spans in the same trace source. Correlate via `vais.correlation.id` when the operator thread context carries one (typically from the incoming CR's annotation).

## v0.14 — OPA policy engine

`OpaPolicyEngine` (`Vais.Agents.Control.Policy.Opa`) emits a `Vais.Agents.Policy.OPA` activity per `EvaluateAsync` call:

| Tag | Source | Values |
|---|---|---|
| `vais.policy.operation` | `PolicyOperation` enum | `Create` / `Invoke` / `Signal` / `Query` / `Cancel` / `Update` / `Evict` |
| `vais.policy.agent.id` | `AgentManifest.Id` when non-null | opaque |
| `vais.policy.agent.version` | `AgentManifest.Version` when non-null | opaque |
| `vais.policy.principal.tenant` | `AgentPrincipal.TenantId` when non-null | opaque |
| `vais.policy.cache-hit` | decision-cache hit? | `True` / `False` |
| `vais.policy.decision` | result | `allow` / `deny` |
| `vais.policy.deny-reason` | reason from Rego on deny | opaque |
| `vais.policy.opa.status-code` | OPA HTTP response code on `200` / `5xx` | `200`, `5xx`; absent on transport failure |

Deny is a span-status `Error` with the reason as description — standard OTel conventions. A `Denied` dashboard filters on `vais.policy.decision = "deny"` with `vais.policy.deny-reason` as the breakdown.

## Constants in code

All `vais.*` names are available as `const string` in `Vais.Agents.Core.AgenticTags` (core agent tags) + `AgenticTags.Control.*` / `AgenticTags.Stream.*` / `AgenticTags.Operator.*` / `AgenticTags.Policy.*` (post-v0.6 families, declared alongside their emitting packages):

```csharp
using Vais.Agents.Core;

// Don't type strings — use:
activity.SetTag(AgenticTags.UserId, ctx.UserId);
activity.SetTag(AgenticTags.Policy.Decision, "deny");
```

`gen_ai.*` names are the spec; they aren't re-exported as constants (OpenTelemetry's own `SemanticConventions` package carries them).

## See also

- [Observability concept](../concepts/observability.md)
- [ADR 0002 — OTel GenAI conventions](../adr/0002-otel-genai-conventions.md)
- [Deploy OTel and Langfuse guide](../guides/deploy-otel-and-langfuse.md)
- [OPA policy engine concept](../concepts/opa-policy-engine.md) — per-evaluation span shape.
- [Kubernetes operator concept](../concepts/kubernetes-operator.md) — reconcile span shape.
