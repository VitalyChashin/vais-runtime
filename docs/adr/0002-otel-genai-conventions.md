# ADR 0002: OpenTelemetry GenAI semantic conventions

- **Status:** Accepted — 2026-04-17 (M2b)
- **Context bounded by:** Phase 1 of the Vais2.Agents OSS extraction (`plans/actor-agents-oss-extraction-research.md` in the parent VAIS2 repo).
- **Supersedes:** the ad-hoc `llm.*` activity tags used by VAIS2's `Vais2Agents.Orleans`.

## Context

`StatefulAiAgent` already owns per-turn timing and usage metadata (it builds a `UsageRecord` per turn). To make that data visible to off-the-shelf OTel collectors (Jaeger, Tempo, Grafana, Datadog, Langfuse), we have to commit to a specific activity / metric naming convention.

The OpenTelemetry semantic-convention group has a *GenAI* specification under active development. It nails down:

- **`gen_ai.system`** — the provider identifier (`openai`, `azure.openai`, `anthropic`, …).
- **`gen_ai.request.model`** — the model the caller asked for.
- **`gen_ai.response.model`** — the model the provider actually used (may differ on aliases).
- **`gen_ai.usage.input_tokens`** / **`gen_ai.usage.output_tokens`** — token counts.
- **`gen_ai.operation.name`** — `chat`, `text_completion`, `embeddings`, …
- Span name convention: `{operation} {model}` (e.g. `chat gpt-4o`).
- Metric names: `gen_ai.client.token.usage` (histogram), `gen_ai.client.operation.duration` (histogram).

These names are **not yet stable** — the spec is in *experimental* status as of 2026-Q2 — but both MEAI's `OpenAIChatClient` and MAF's `ChatClientAgent` already emit in this shape. If we adopt the same names, our spans and theirs line up in a single trace view.

## Decision

1. **Adopt the GenAI conventions verbatim.** We do not invent `vais2.*` or `llm.*` tags where a GenAI name exists.
2. **ActivitySource name: `"Vais2.Agents"`.** Exposed as `AgenticDiagnostics.ActivitySourceName`. Consumers call `.AddSource("Vais2.Agents")` on their `TracerProviderBuilder`, or use our `AddAgenticInstrumentation()` extension which does it for them.
3. **Meter name: `"Vais2.Agents"`.** Same string, different telemetry pillar — consumers reading the constant get both by referencing `AgenticDiagnostics.MeterName`.
4. **Span per `AskAsync` turn.** Activity name: `"chat"` (the operation), with `gen_ai.request.model` / `gen_ai.response.model` attached as tags. Kind: `Client`.
5. **Metrics emitted by `OpenTelemetryUsageSink`:**
   - Histogram `gen_ai.client.token.usage` — dimensions `gen_ai.system`, `gen_ai.response.model`, `gen_ai.token.type` (`input` or `output`).
   - Histogram `gen_ai.client.operation.duration` — dimensions `gen_ai.system`, `gen_ai.response.model`, `gen_ai.operation.name`, `error.type` (on failure).
6. **Vais2-specific extensions use the `vais2.*` prefix**, clearly separated. `vais2.agent.name`, `vais2.user.id`, `vais2.tenant.id`, `vais2.correlation.id`. The Langfuse enricher (see §2b in the research doc) translates these onto the `langfuse.*` names Langfuse expects.
7. **Status is tracked in the span.** Success → `ActivityStatusCode.Ok`. Failure → `ActivityStatusCode.Error` with the short exception type name as `error.type`.
8. **Zero-cost when no listener.** `ActivitySource.StartActivity(...)` returns null when no listener is registered; `Vais2.Agents.Core` tolerates this. Consumers who never reference `Vais2.Agents.Observability.OpenTelemetry` pay no allocation.

## Why not ...

| Option | Why rejected |
|---|---|
| Invent our own `vais2.llm.*` tag scheme | Consumers already have OTel-aware tooling; a custom scheme means every exporter needs a translation rule. |
| Port VAIS2's `llm.*` tags as-is | These predate the GenAI spec; they ossify a naming that no downstream vendor supports. |
| Wait for GenAI to graduate to stable | The names have been steady for ~6 months and MEAI/MAF already emit them. Renaming later is a mechanical sweep if the spec shifts. |
| Emit per-message spans (one per ChatTurn) | Too noisy. One span per `AskAsync` turn is the natural unit; if a caller wants per-message detail they can add their own `IAgentFilter` and emit child spans. |

## Consequences

- **Positive:** spans line up with MEAI- and MAF-native spans in a single trace view — no correlation-ID hacks required.
- **Positive:** `OpenTelemetryUsageSink` is a thin translation layer from `UsageRecord` → OTel primitives. No business logic lives there.
- **Negative:** the GenAI spec is experimental. If a tag is renamed upstream, we rename and bump a minor. We accept this — the cost of drift is a search-and-replace.
- **Negative:** tag-name typos are still runtime-only (they're strings). Mitigated by centralising them in `AgenticDiagnostics` constants so consumers never type the names themselves.

## Follow-ups

- M2c (tool-calling parity) will add child spans for tool invocations, following the GenAI spec's tool-call attributes (`gen_ai.tool.name`, `gen_ai.tool.call.id`).
- Langfuse enricher (W6 / M2b) rides on top of the GenAI tags — it doesn't replace them. It adds `langfuse.*` aliases for the Langfuse UI.
- Orleans host (M3) will propagate the `vais2.correlation.id` tag from grain context into the ActivitySource.
