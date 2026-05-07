# LlmGatewayMiddleware

Compose LLM gateway middleware in C# — fallback, semantic cache, structured-output (JSON) validation. Three scripted passes on separate `StatefulAiAgent` instances; no API key required.

## Prerequisites

The v0.40 gateway packages are in the source tree but must be packed before they appear in the local feed:

```bash
dotnet pack src/Vais.Agents.Gateways.Fallback         -o artifacts/packages/
dotnet pack src/Vais.Agents.Gateways.SemanticCache    -o artifacts/packages/
dotnet pack src/Vais.Agents.Gateways.StructuredOutput -o artifacts/packages/
```

## Run

```bash
dotnet run --project samples/LlmGatewayMiddleware
```

## Expected output

```
== 1 — LlmFallbackMiddleware (primary throws → backup takes over) ==
  response:       "Backup reply."
  flaky attempts: 1  (threw on attempt 1)
  backup used:    True

== 2 — LlmSemanticCacheMiddleware (same text → cache hit on 2nd call) ==
  call 1: "Paris."
  call 2: "Paris."  (served from cache)
  provider called: 1 time(s)  (expected 1)
  bodies match:    True

== 3 — LlmJsonOutputMiddleware<WeatherReport> (JSON output validated) ==
  raw text:  {"city":"Tokyo","tempC":18,"condition":"Sunny"}
  city=Tokyo  tempC=18  condition=Sunny

Done.
```

## What it demonstrates

- `StatefulAgentOptions.GatewayMiddleware` — `IReadOnlyList<LlmGatewayMiddleware>` on `StatefulAgentOptions`. Set inline with a collection expression; entries are applied as agent filters around every completion call.
- `LlmFallbackMiddleware(IFallbackProviderPool)` — bypasses `next` and tries each provider in `InMemoryFallbackProviderPool` in order until one succeeds. If all fail, the last exception is re-thrown. The primary provider passed to `StatefulAiAgent` is irrelevant when this middleware is active — the pool drives provider selection.
- `LlmSemanticCacheMiddleware(ISemanticCacheStore)` — caches responses keyed on the last user-turn text. Cache miss: calls `next` (the provider) and stores the response. Cache hit: returns stored `CompletionResponse` without calling `next`. `InMemorySemanticCacheStore` provides a zero-config in-process implementation.
- `LlmJsonOutputMiddleware<T>` — validates that the provider's response text can be deserialized to `T` using `System.Text.Json`. Throws `AgentGuardrailDeniedException` if the text is not valid JSON for `T`. The validated text is returned unchanged in `CompletionResponse.Text`; callers deserialize manually.
- Gateway middleware composes with other `StatefulAgentOptions` features (guardrails, streaming filters) — they operate at different layers of the turn pipeline.

## Docs

- [LLM gateway concept](../../docs/concepts/llm-gateway.md)
- [`McpGatewayMiddleware`](../McpGatewayMiddleware) — companion sample for tool gateway middleware
- [`declarative-agent-mcp-gateways`](../declarative-agent-mcp-gateways) — same gateway pipelines via YAML manifests (no C# required)
