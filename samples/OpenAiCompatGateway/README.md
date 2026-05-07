# OpenAiCompatGateway

Expose a Vais.Agents provider as a `POST /v1/chat/completions` endpoint compatible with any OpenAI SDK or curl command. The sample boots an in-process ASP.NET Core host, routes the `gpt-4o-mini` alias to a scripted provider, then drives three client calls — `GET /v1/models`, non-streaming completion, and SSE streaming — against it.

## Prerequisites

The gateway package must be packed before it appears in the local feed:

```bash
dotnet pack src/Vais.Agents.Gateways.OpenAiCompat -o artifacts/packages/
```

## Run

```bash
dotnet run --project samples/OpenAiCompatGateway
```

## Expected output

```
Server: http://127.0.0.1:<port>

== GET /v1/models ==
  gpt-4o-mini

== POST /v1/chat/completions (stream: false) ==
  status:  200
  object:  chat.completion
  content: "4"
  finish:  stop
  usage:   prompt=10  completion=1

== POST /v1/chat/completions (stream: true) ==
  status:       200
  content-type: text/event-stream
  content:      "1 2 3"

Done.
```

## What it demonstrates

- `AddOpenAiCompatGateway()` — registers `AsyncLocalAgentContextAccessor`; all other DI (identity resolver, model router) is added separately.
- `AddPassThroughIdentityResolver()` — dev-only resolver that accepts any `Authorization: Bearer <token>` value; safe for local scripted samples, not production.
- `AddInMemoryModelRouter(routes => ...)` — maps model alias strings (`"gpt-4o-mini"`) to `ModelRoute(ICompletionProvider, ModelSpec)` pairs.
- `MapOpenAiCompat()` — mounts `POST /v1/chat/completions` + `GET /v1/models`; checks whether the resolved provider implements `IStreamingCompletionProvider` for `stream: true` requests.
- `ICompletionProvider` / `IStreamingCompletionProvider` — a single class can implement both; the gateway dispatches to `StreamAsync` when `stream: true`, `CompleteAsync` otherwise.
- OpenAI wire format — `GET /v1/models` returns `{"object":"list","data":[{"id":"gpt-4o-mini",...}]}`; non-streaming `POST` returns a `chat.completion` object with `choices`, `usage`, `finish_reason`; streaming `POST` returns SSE `data: {chunk}\n\n` lines, closing with `data: [DONE]\n\n`.
- SSE consumption with `HttpCompletionOption.ResponseHeadersRead` — read the response stream line-by-line before it completes; extract `choices[0].delta.content` from each `data: ` line.

## Docs

- [OpenAI-compatible gateway](../../docs/guides/openai-compat-gateway.md)
- [`declarative-agent-mcp-gateways`](../declarative-agent-mcp-gateways) — same gateway configured via YAML manifests
- [`LlmGatewayMiddleware`](../LlmGatewayMiddleware) — LLM gateway middleware (fallback, cache, structured output)
