# Guide: stream invocations over HTTP

`POST /v1/agents/{id}/invoke/stream` returns a `text/event-stream` Server-Sent Events response. Every `AgentEvent` the agent emits rides the wire as a discrete SSE frame — text deltas, tool dispatch, guardrail decisions, interrupts, handoff. The client's `InvokeStreamEventsAsync` yields typed `AgentEvent` records; `InvokeStreamAsync` is the text-only projection.

Shipped in v0.12 as the `InvokeStream` route in `Vais.Agents.Control.Http.Server` + `IStreamingAiAgent` capability interface + `CompletionDelta : AgentEvent` record in `Vais.Agents.Abstractions` + client-side parsing via `System.Net.ServerSentEvents 10.0.2`.

## Packages

```xml
<PackageReference Include="Vais.Agents.Abstractions" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Control.Http.Server" Version="0.15.0-preview" />
<PackageReference Include="Vais.Agents.Control.Http.Client" Version="0.15.0-preview" />
```

## Server-side — wire the route

`MapAgentControlPlane` automatically exposes both the unary and streaming routes. The streaming path is mounted with a `[StreamingEndpoint]` attribute so the v0.11 idempotency middleware bypasses it (the body is an unbounded event stream — impractical to fingerprint).

```csharp
using Vais.Agents.Control.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgentControlPlane();
builder.Services.Configure<StreamingInvokeOptions>(o =>
{
    o.HeartbeatInterval = TimeSpan.FromSeconds(15);   // default — keeps proxies from idling the connection
});

var app = builder.Build();
app.MapAgentControlPlane();                           // exposes /v1/agents/{id}/invoke + /v1/agents/{id}/invoke/stream
app.Run();
```

`HeartbeatInterval` controls the keep-alive cadence. The server emits an SSE comment (`: heartbeat <utc-timestamp>`) at every interval boundary when no real events are flowing — enough to keep load balancers, reverse proxies, and CDNs from idling the connection but small enough to ignore client-side (comment lines land outside the event dispatch).

## The agent must implement `IStreamingAiAgent`

The streaming route probes the resolved agent for the capability interface. If the agent doesn't implement it, the server returns `501 Not Implemented` with `urn:vais-agents:streaming-not-supported`.

> **Orleans (v0.35).** `OrleansAiAgentProxy` implements `IStreamingAiAgent` as of v0.35. Grain-hosted agents no longer return `501` — `IAiAgentGrain.StreamAgentAsync` uses an `IAsyncEnumerable<AgentEvent>` Orleans 10.x native return and `AgentEventSurrogate` carries `CompletionDelta` frames (kind=9). No consumer changes are required; the `POST /invoke/stream` route routes through the proxy transparently.

```csharp
public sealed class WeatherAgent : IAiAgent, IStreamingAiAgent
{
    public Task<string> AskAsync(string userMessage, CancellationToken ct = default) { … }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new TurnStarted(DateTimeOffset.UtcNow, context, userMessage);
        // … provider calls, tool dispatches, delta emission …
        yield return new TurnCompleted(DateTimeOffset.UtcNow, context, finalText, …);
    }
}
```

`StatefulAiAgent` in `Vais.Agents.Core` already implements both interfaces — host that directly and streaming works out of the box.

## Client-side — two shapes

### `InvokeStreamAsync` — text-only projection

When you just want the assistant text and don't care about tool rounds or timestamps:

```csharp
using Vais.Agents.Control.Http;

var client = ClientFactory.Create(config: vaisConfig);

await foreach (var chunk in client.InvokeStreamAsync(
    agentId: "weather",
    request: new AgentInvocationRequest(Text: "What's the weather in Tokyo?"),
    version: null,
    idempotencyKey: null,
    cancellationToken: ct))
{
    Console.Write(chunk);   // concatenate for the complete reply
}
Console.WriteLine();
```

Under the hood this subscribes to the full event stream and filters for `CompletionDelta` frames, yielding `TextDelta` strings. Metadata updates (`ModelId`, token counts) are discarded — use the full event method if you need them.

### `InvokeStreamEventsAsync` — full event hierarchy

```csharp
await foreach (var evt in client.InvokeStreamEventsAsync(
    agentId: "weather",
    request: new AgentInvocationRequest(Text: "What's the weather in Tokyo? Then email a summary."),
    version: null,
    idempotencyKey: Guid.NewGuid().ToString("N"),      // Idempotency-Key allowed on stream too
    cancellationToken: ct))
{
    switch (evt)
    {
        case TurnStarted s:
            Console.WriteLine($"[start]  {s.UserMessage}"); break;
        case CompletionDelta d:
            Console.Write(d.TextDelta); break;
        case ToolCallStarted s:
            Console.WriteLine($"\n[tool→]  {s.ToolName} (call {s.CallId})"); break;
        case ToolCallCompleted c:
            Console.WriteLine($"[tool✓]  {c.ToolName} in {c.Duration.TotalMilliseconds:0}ms"); break;
        case ToolCallReplayed r:
            Console.WriteLine($"[tool↺]  {r.ToolName} (replayed from journal)"); break;
        case GuardrailTriggered g:
            Console.WriteLine($"[guard]  {g.Layer} → {g.Decision}"); break;
        case InterruptRaised i:
            Console.WriteLine($"[halt]   {i.Reason}"); break;
        case HandoffRequested h:
            Console.WriteLine($"[handoff] {h.Handoff.FromAgent} → {h.Handoff.ToAgent}"); break;
        case TurnCompleted c:
            Console.WriteLine($"\n[done]   in {c.Duration.TotalSeconds:0.0}s, {c.CompletionTokens} tokens out"); break;
        case TurnFailed f:
            Console.WriteLine($"\n[fail]   {f.ErrorType}: {f.ErrorMessage}"); break;
    }
}
```

Every subtype in the [`AgentEvent` taxonomy](../reference/events.md) fires over the wire — ten event names in total.

## SSE wire-event mapping

Each AgentEvent subtype maps to a stable `event:` field on the SSE frame:

| `AgentEvent` subtype | Wire event name |
|---|---|
| `TurnStarted` | `turn.started` |
| `TurnCompleted` | `turn.completed` |
| `TurnFailed` | `turn.failed` |
| `ToolCallStarted` | `tool.started` |
| `ToolCallCompleted` | `tool.completed` |
| `ToolCallReplayed` | `tool.replayed` |
| `GuardrailTriggered` | `guardrail.triggered` |
| `InterruptRaised` | `interrupt.raised` |
| `HandoffRequested` | `handoff.requested` |
| `CompletionDelta` | `delta` |

The `data:` field carries JSON for the subclass body.

Raw example (token deltas trimmed):

```
event: turn.started
data: {"at":"2026-04-20T10:15:00Z","context":{"agentName":"weather"},"userMessage":"What's the weather in Tokyo?"}

event: delta
data: {"at":"2026-04-20T10:15:01Z","context":{"agentName":"weather"},"textDelta":"Checking"}

event: delta
data: {"at":"2026-04-20T10:15:01Z","context":{"agentName":"weather"},"textDelta":" the"}

: heartbeat 2026-04-20T10:15:16Z

event: tool.started
data: {"at":"2026-04-20T10:15:02Z","context":{"agentName":"weather"},"callId":"call-1","toolName":"get_weather"}

event: tool.completed
data: {"at":"2026-04-20T10:15:03Z","context":{"agentName":"weather"},"callId":"call-1","toolName":"get_weather","succeeded":true,"duration":"00:00:01.123"}

event: delta
data: {"at":"2026-04-20T10:15:04Z","context":{"agentName":"weather"},"textDelta":"It's 18°C and sunny in Tokyo."}

event: turn.completed
data: {"at":"2026-04-20T10:15:04Z","context":{"agentName":"weather"},"assistantText":"It's 18°C and sunny in Tokyo.","modelId":"gpt-4o","promptTokens":42,"completionTokens":12,"duration":"00:00:04"}
```

Each frame is a standard SSE entry — `event:` + `data:` + blank-line separator. Comments (`: …`) are heartbeats + ignored by the parser.

## Cancellation

The SSE stream honours request cancellation cleanly. On the client, pass a `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource();

_ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => cts.Cancel());   // hard cap at 30s

await foreach (var evt in client.InvokeStreamEventsAsync(agentId, request, null, null, cts.Token))
{
    // …
}
```

On the server, `HttpContext.RequestAborted` fires when the client goes away. The route handler propagates this into the agent's `StreamAsync` cancellation token — in-flight tool dispatches abort, the enumerator completes, and the server writes no further frames.

## Idempotency + streaming

`Idempotency-Key` is accepted on the streaming route, but the idempotency middleware bypasses streaming endpoints (marked via `[StreamingEndpoint]`). The key is passed through to the agent for its own correlation — useful when the downstream agent implementation wants request-scoped replay semantics, but the HTTP middleware does not cache responses.

## Limitations

- **Orleans-silo streaming passthrough deferred.** In v0.12, Orleans-hosted agents that only expose `IAgentGrain` (not a direct `IStreamingAiAgent` impl) return `501 urn:vais-agents:streaming-not-supported`. The Orleans grain surface gets an `IAsyncEnumerable<AgentEvent>` method in a later pillar.
- **Reconnect on mid-stream drop loses events.** No resumable-stream semantics in v0.12 (no `Last-Event-ID` support). If a connection drops mid-turn, retry with a fresh `Idempotency-Key` and replay from scratch — the text-stream deltas cannot be "resumed" from a partial offset.
- **Buffer-bloat on slow consumers.** The server does not apply backpressure — a consumer slower than the event rate accumulates frames in transport buffers. Pair with `HeartbeatInterval` and a sane upstream proxy flush cadence.

## See also

- [Stream with tools guide](stream-with-tools.md) — in-process `StatefulAiAgent.StreamAsync` walkthrough; this guide is the HTTP projection of the same event stream.
- [Events reference](../reference/events.md) — `AgentEvent` hierarchy + wire-event-name table.
- [Execution loop concept](../concepts/execution-loop.md) — where each event fires during the run.
- [Control plane concept](../concepts/control-plane.md) — where the streaming route sits in the verb set.
- [ADR 0004: SSE event taxonomy on the wire](../adr/0004-sse-event-taxonomy-on-wire.md) — why the full AgentEvent hierarchy rides the wire instead of just text deltas.
- [`samples/HttpStreamingInvoke`](../../samples/HttpStreamingInvoke) — runnable walkthrough.
