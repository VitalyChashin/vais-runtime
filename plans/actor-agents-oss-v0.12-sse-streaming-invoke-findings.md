# v0.12 SSE streaming Invoke — spike findings

Synthesis of the research spike scoped in [`actor-agents-oss-v0.12-sse-streaming-invoke-spike.md`](./actor-agents-oss-v0.12-sse-streaming-invoke-spike.md). Answers Q1–Q5 with evidence, not opinion. Landing verdict at the bottom.

Created 2026-04-20. **Status**: complete. Q1–Q5 resolved from code audit + middleware content-type ordering check + `AgentEvent` shape audit + SSE parser availability check.

---

## Q1 — Route shape

### Candidates scored

| option | url shape | OpenAPI legibility | idempotency-middleware compat | consumer surprise |
|---|---|---|---|---|
| **(a) Dedicated route** | `POST /v1/agents/{id}/invoke/stream` | clean (separate operationId `Agents.InvokeStream`, separate `.Produces<T>` on each) | server sets `text/event-stream` → middleware opts out by content-type | low — different URL = different response shape |
| **(b) Accept-header negotiation** | `POST /v1/agents/{id}/invoke` + `Accept: text/event-stream` | one operation with two response bodies — OpenAPI supports it but clients often miss it | middleware sees response content-type either way — works | medium — same URL yields different shapes depending on header |
| **(c) Query-param opt-in** | `POST /v1/agents/{id}/invoke?stream=true` | operation parameters enum the stream flag, but response still varies | works | medium — `?stream=true` is GCP-style; less common in REST |

### Middleware content-type ordering validation

`AgentControlPlaneIdempotencyMiddleware.cs:184` inspects `context.Response.ContentType` **after** `next(context)` returns. The endpoint must set the content-type before any `await` that yields back to middleware. .NET 9 minimal-API returning `Results.Stream(...)` / custom `IResult` sets content-type eagerly; a handler that starts with `context.Response.ContentType = "text/event-stream";` before the first yield is correct. Confirmed: middleware's `isStreaming` check fires correctly when content-type is set first.

### Decision (Q1): **(a) dedicated route** — `POST /v1/agents/{id}/invoke/stream`

Cleanest OpenAPI rendering; natural separation of operations; middleware opt-out by content-type is automatic.

---

## Q2 — Event taxonomy on the wire

### `AgentEvent` shape audit

Read `AgentEvent.cs`. Nine concrete subtypes shipped; XML doc explicitly says **"Closed hierarchy: adding a new subtype is an unshipped addition to Abstractions, not a subclass in downstream code"** — so we can add one new subtype in v0.12. Existing subtypes:

| subtype | fields (beyond `At + Context`) | SSE event name |
|---|---|---|
| `TurnStarted` | `UserMessage` | `turn.started` |
| `TurnCompleted` | `AssistantText`, `ModelId?`, `PromptTokens?`, `CompletionTokens?`, `Duration` | `turn.completed` |
| `TurnFailed` | `ErrorType`, `ErrorMessage`, `Duration` | `turn.failed` |
| `ToolCallStarted` | `CallId`, `ToolName` | `tool.started` |
| `ToolCallCompleted` | `CallId`, `ToolName`, `Succeeded`, `Error?`, `Duration` | `tool.completed` |
| `GuardrailTriggered` | `Layer`, `Decision`, `Reason?` | `guardrail.triggered` |
| `InterruptRaised` | `InterruptId`, `Reason` | `interrupt.raised` |
| `HandoffRequested` | `Handoff` | `handoff.requested` |
| `ToolCallReplayed` | `CallId`, `ToolName` | `tool.replayed` |
| **new**: `CompletionDelta` | `TextDelta`, `ModelId?`, `PromptTokens?`, `CompletionTokens?`, `ToolCalls?` | `delta` |

`CompletionDelta` mirrors `CompletionUpdate`'s shape. Internal `StatefulAiAgent.StreamAsync` already emits the existing 4 `Turn*`/`Tool*` events to `_eventBus`; the new `CompletionDelta` fires on each yielded text chunk.

### SSE transcript — one turn with a tool call

```
event: turn.started
data: {"at":"2026-04-20T12:00:00.000Z","context":{"runId":"run-1","userId":"u1"},"userMessage":"what's the weather?"}

event: tool.started
data: {"at":"2026-04-20T12:00:00.300Z","context":{"runId":"run-1"},"callId":"call-1","toolName":"get_weather"}

event: tool.completed
data: {"at":"2026-04-20T12:00:00.420Z","context":{"runId":"run-1"},"callId":"call-1","toolName":"get_weather","succeeded":true,"error":null,"duration":"00:00:00.1200000"}

event: delta
data: {"textDelta":"The weather is "}

event: delta
data: {"textDelta":"sunny, 72°F."}

event: turn.completed
data: {"at":"2026-04-20T12:00:01.500Z","context":{"runId":"run-1"},"assistantText":"The weather is sunny, 72°F.","modelId":"gpt-4","promptTokens":145,"completionTokens":8,"duration":"00:00:01.5000000"}
```

Between events, the server can write heartbeat comments:

```
: heartbeat 2026-04-20T12:00:15.000Z

```

SSE `event:` field is the discriminator — JSON body doesn't carry a type field. Consumer dispatches on event name.

### Decision (Q2): **(c) full `AgentEvent` taxonomy** + new `CompletionDelta` subtype

Ten event kinds on the wire. `event:` field maps to the kebab-cased event name (`turn.started`, `tool.completed`, `delta`, etc.). Body is the concrete record's JSON shape.

---

## Q3 — Server-side attachment model

### Capability interface — locked

```csharp
namespace Vais.Agents;

public interface IStreamingAiAgent
{
    /// <summary>
    /// Stream the next turn(s) as an <see cref="IAsyncEnumerable{AgentEvent}"/>.
    /// Emits <see cref="TurnStarted"/> first, then <see cref="CompletionDelta"/>
    /// per yielded text chunk (plus tool-call events as they dispatch), terminating
    /// on <see cref="TurnCompleted"/> or <see cref="TurnFailed"/>.
    /// </summary>
    IAsyncEnumerable<AgentEvent> StreamAsync(
        string userMessage,
        AgentContext context,
        CancellationToken cancellationToken);
}
```

- `StatefulAiAgent` implements it. The existing `public async IAsyncEnumerable<string> StreamAsync(string, CT)` stays (source-compat); a new internal helper drives the event-emitting loop; the string-returning method delegates and projects to `CompletionDelta.TextDelta`.
- `OrleansAiAgentProxy` does NOT implement it in v0.12 (Orleans streaming passthrough stays deferred). HTTP endpoint checks `is IStreamingAiAgent` and returns 501 otherwise.

### Cancellation + disposal

- `HttpContext.RequestAborted` → passed into the endpoint handler → passed into `StreamAsync(ct)` → flows through the provider-streaming call.
- Client closes connection → `RequestAborted` fires → the per-turn retry boundary's pipeline callback sees CT → agent exits gracefully.
- On exit: `StatefulAiAgent`'s existing `finally` blocks dispose the enumerator + emit `TurnFailed` (for OCE) or nothing (for abort). SSE stream closes.

### Decision (Q3): **(c) capability interface** — `IStreamingAiAgent` in Abstractions; `StatefulAiAgent` implements; Orleans proxy deferred to future pillar

Matches v0.9's `IResumableAgentGraph<TState>` precedent. Zero churn on `IAgentLifecycleManager` / `IAgentRuntime` / `IAiAgent`.

---

## Q4 — Client-side parse loop

### SSE parser choice

`System.Net.ServerSentEvents` 10.0.2 is already transitively available (pulled through `Microsoft.SemanticKernel` via OpenAI SDK). `SseParser<T>.Create(stream, itemParser)` gives us an `IAsyncEnumerable<SseItem<T>>` wrapping the response body. Target frame is a concrete `AgentEvent`; parser dispatches by `SseItem.EventType` to the right subtype via a switch on the event name.

**Net.ServerSentEvents 10.0.2 compatibility with net9.0**: yes — package targets net6.0/net8.0/net9.0 TFMs. Verified via transitive consumption from SK (which targets netstandard2.0) already in our solution.

### Client shape — two overloads

```csharp
// On IAgentControlPlaneClient:

/// <summary>Stream an invocation as a sequence of text deltas. Filters the full event
/// stream to CompletionDelta.TextDelta.</summary>
IAsyncEnumerable<string> InvokeStreamAsync(
    string agentId,
    AgentInvocationRequest request,
    string? version,
    string? idempotencyKey,
    CancellationToken cancellationToken);

/// <summary>Stream an invocation as the full AgentEvent taxonomy.</summary>
IAsyncEnumerable<AgentEvent> InvokeStreamEventsAsync(
    string agentId,
    AgentInvocationRequest request,
    string? version,
    string? idempotencyKey,
    CancellationToken cancellationToken);
```

Both overloads are DIM on the interface — default impls throw `NotSupportedException` so mock implementations don't need to care. Concrete `AgentControlPlaneClient` overrides; `InvokeStreamAsync` (text-only) is a thin `OfType<CompletionDelta>().Select(d => d.TextDelta)` over `InvokeStreamEventsAsync`.

### Parse-loop sketch

```csharp
public async IAsyncEnumerable<AgentEvent> InvokeStreamEventsAsync(
    string agentId,
    AgentInvocationRequest request,
    string? version,
    string? idempotencyKey,
    [EnumeratorCancellation] CancellationToken cancellationToken)
{
    var path = version is null
        ? $"/v1/agents/{Uri.EscapeDataString(agentId)}/invoke/stream"
        : $"/v1/agents/{Uri.EscapeDataString(agentId)}/invoke/stream?version={Uri.EscapeDataString(version)}";
    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
    {
        Content = JsonContent.Create(request, options: JsonOptions),
    };
    httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    AttachIdempotencyKey(httpRequest, idempotencyKey);

    using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
        .ConfigureAwait(false);
    await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

    var contentType = response.Content.Headers.ContentType?.MediaType;
    if (!string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
    {
        throw new AgentControlPlaneException(
            (int)response.StatusCode,
            type: null,
            title: "Unexpected content type",
            detail: $"Expected text/event-stream, got {contentType}.");
    }

    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    var parser = SseParser.Create<AgentEvent?>(stream, ParseAgentEvent);
    await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
    {
        if (item.Data is { } evt)
        {
            yield return evt;
        }
    }
}

private static AgentEvent? ParseAgentEvent(string eventType, ReadOnlySpan<byte> data)
    => eventType switch
    {
        "turn.started"         => JsonSerializer.Deserialize<TurnStarted>(data, JsonOptions),
        "turn.completed"       => JsonSerializer.Deserialize<TurnCompleted>(data, JsonOptions),
        "turn.failed"          => JsonSerializer.Deserialize<TurnFailed>(data, JsonOptions),
        "tool.started"         => JsonSerializer.Deserialize<ToolCallStarted>(data, JsonOptions),
        "tool.completed"       => JsonSerializer.Deserialize<ToolCallCompleted>(data, JsonOptions),
        "tool.replayed"        => JsonSerializer.Deserialize<ToolCallReplayed>(data, JsonOptions),
        "guardrail.triggered"  => JsonSerializer.Deserialize<GuardrailTriggered>(data, JsonOptions),
        "interrupt.raised"     => JsonSerializer.Deserialize<InterruptRaised>(data, JsonOptions),
        "handoff.requested"    => JsonSerializer.Deserialize<HandoffRequested>(data, JsonOptions),
        "delta"                => JsonSerializer.Deserialize<CompletionDelta>(data, JsonOptions),
        _                      => null, // unknown event type — skip (forward-compat)
    };
```

### Decision (Q4): **ship both overloads + use `System.Net.ServerSentEvents` built-in parser**

Zero new deps (package is already transitively pulled). `InvokeStreamAsync` text-only + `InvokeStreamEventsAsync` full events.

---

## Q5 — Heartbeat + cancellation + Orleans + 501

### Heartbeat mechanics

Long pauses (slow tool call, provider stalls) can look like dead connections to proxies/load balancers. Standard fix: emit `: <comment>\n\n` comments every N seconds. SSE spec treats comment lines as no-ops — clients ignore them, but proxies see TCP activity and don't close.

Implementation:
- Channel-based multiplex: `Channel<string>` unbounded. Agent-event loop writes `event: X\ndata: Y\n\n` strings; heartbeat timer writes `: heartbeat <timestamp>\n\n` every 15s.
- Single SSE-writer task drains the channel to `context.Response.Body`.
- Linked CTS: `cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted)`. Timer + agent loop + writer all honour the linked token.
- On disposal (finally): cancel CTS → timer stops → writer drains + completes → channel closes.

Options: `StreamingInvokeOptions.HeartbeatInterval = TimeSpan.FromSeconds(15)` (configurable; `TimeSpan.Zero` disables).

### Cancellation

- Client close → `RequestAborted` → linked CTS → all three loops exit.
- Middleware cleanup: the per-turn retry-boundary's Polly pipeline sees the linked CT; filter-domain exceptions and OCE propagate as documented in v0.10.
- No `TurnFailed` on clean cancel (matches `AskAsync` v0.4 behaviour — OCE is not a failure).

### Orleans 501 path

Registry → manifest → `runtime.GetOrCreate(id)` returns an `IAiAgent`. If it's not `IStreamingAiAgent`:

```csharp
return ProblemDetailsMapping.StreamingNotSupported(agentId);
// → 501 Not Implemented, type=urn:vais-agents:streaming-not-supported,
//   detail="Agent '{agentId}' is hosted on a runtime that does not support streaming.
//           Use POST /v1/agents/{id}/invoke for buffered responses."
```

New URN constant + factory helper added to `ProblemDetailsMapping` (mirrors v0.11's two idempotency URNs).

### Content-type ordering (Q1 follow-up)

Endpoint handler sequence:
1. Authenticate + validate (middleware).
2. Check agent is `IStreamingAiAgent` → 501 if not.
3. Set `context.Response.ContentType = "text/event-stream"` + `context.Response.Headers["Cache-Control"] = "no-cache"` + `context.Response.Headers["X-Accel-Buffering"] = "no"` (disable nginx buffering).
4. Start heartbeat timer on linked CTS.
5. Spawn agent loop on linked CTS; writes events to channel.
6. Drain channel to response body until CTS or agent-loop exit.
7. Finally: cancel CTS, dispose timer, wait for drain.

Idempotency middleware sees content-type = `text/event-stream` in its post-`next(context)` inspection → `isStreaming = true` → skips CompleteAsync, calls ReleaseAsync. Streamed responses don't get cached. **No middleware changes needed** — the v0.11 hook already does the right thing.

### Decision (Q5): **15s heartbeat default (configurable); linked CTS; 501+URN for non-streaming agents; content-type set first**

---

## Verdict — ready to write the pillar plan

### Locked decisions

1. **Route = `POST /v1/agents/{id}/invoke/stream`** (dedicated, v0.6 plan stands).
2. **Event taxonomy = all existing `AgentEvent` subtypes + new `CompletionDelta`**. SSE `event:` field is the discriminator; 10 event kinds total on the wire.
3. **`IStreamingAiAgent` capability interface in Abstractions.** `StatefulAiAgent` implements; `OrleansAiAgentProxy` does NOT (deferred); HTTP endpoint returns 501+URN on non-streaming-capable agents.
4. **`CompletionDelta : AgentEvent`** — new closed-hierarchy subtype carrying `TextDelta` + optional `ModelId` / `PromptTokens` / `CompletionTokens` / `ToolCalls` (mirrors `CompletionUpdate` shape).
5. **Two client overloads**: `InvokeStreamAsync(...) : IAsyncEnumerable<string>` (text-only) + `InvokeStreamEventsAsync(...) : IAsyncEnumerable<AgentEvent>` (full taxonomy). DIM defaults throw `NotSupportedException`. Concrete `AgentControlPlaneClient` overrides both; text-only is a thin `OfType<CompletionDelta>()` filter.
6. **Parser = `System.Net.ServerSentEvents` built-in.** Zero new deps; already transitively available. 10-case switch on `eventType` to the right subtype deserialiser.
7. **Heartbeat via channel multiplex**, default 15s interval. Configurable + disable-able.
8. **New Problem-Details URN**: `urn:vais-agents:streaming-not-supported` (501). Added to `ProblemDetailsMapping` + `VaisProblemDetailsOperationTransformer._urnsByStatus`.
9. **Idempotency middleware unchanged** — v0.11's `text/event-stream` opt-out already does the right thing; SSE endpoints set content-type first.
10. **Orleans deferred** — documented limitation, 501 path. Future pillar covers the proxy.

### Proposed PR shape (4 PRs)

**PR 1 — Abstractions: `CompletionDelta` + `IStreamingAiAgent`.**
- New sealed record `CompletionDelta : AgentEvent` with the 5 extra fields.
- New interface `IStreamingAiAgent` with the 3-arg `StreamAsync`.
- XML doc update on `AgentEvent` to note the +1 subtype (10 → 10 listed).
- PublicAPI: Abstractions +~12 entries (record auto-synthesised members + interface).
- No test changes; shape only.

**PR 2 — Core: implement `IStreamingAiAgent` on `StatefulAiAgent`.**
- Add `IStreamingAiAgent.StreamAsync(string, AgentContext, CT) : IAsyncEnumerable<AgentEvent>` explicit/implicit implementation.
- Internal refactor: new `StreamEventsCore` helper that drives the per-turn loop and yields events as it goes; the existing `StreamAsync(string) : IAsyncEnumerable<string>` delegates + projects to `CompletionDelta.TextDelta`.
- Tests: ~8 new — full-event-stream round-trip on Archetype A (text only), Archetype B (with tool calls), Archetype C (with guardrail denial), cancellation mid-delta yields no `TurnFailed`, `CompletionDelta` shape carries `ModelId` + token counts on the final update, event ordering matches the transcript.
- PublicAPI: Core +1 interface-impl entry.

**PR 3 — Server: HTTP streaming endpoint + client overloads.**
- New endpoint handler `InvokeStreamAsync` mapped to `POST /v1/agents/{id}/invoke/stream` in `AgentControlPlaneEndpointRouteBuilderExtensions`.
- Channel-based multiplex + heartbeat timer + linked CTS + SSE writer.
- `StreamingInvokeOptions` (HeartbeatInterval; room to grow).
- `ProblemDetailsMapping.StreamingNotSupported(agentId)` + `StreamingNotSupportedType` URN const.
- `VaisProblemDetailsOperationTransformer._urnsByStatus` gains `"501"` entry.
- Route annotation `.Produces<AgentEvent>(200, "text/event-stream")` + URN metadata for 501.
- Client: `IAgentControlPlaneClient` gains 2 DIM-default overloads; `AgentControlPlaneClient` overrides both. SSE parser dispatcher. Idempotency-Key threaded (though typically unused for streaming writes).
- Tests: ~12 new — end-to-end SSE round-trip (text-only client), full-events client, 501 on non-streaming proxy, heartbeat fires on long pauses, cancellation propagates, content-type check on response, tool-call events interleaved with text deltas, Problem Details on 501.
- PublicAPI: Http.Server +~8 entries; Http.Client +4 entries (2 class overloads + 2 DIM interface methods).

**PR 4 — v0.12.0-preview cut.**
- API freeze on 4 packages (Abstractions + Core + Http.Server + Http.Client).
- Pack 22 packages at `0.12.0-preview`.
- Smoketest bump + SSE probe (spin up TestServer-equivalent in-process, POST to stream, consume one event, assert shape).
- Tag `v0.12.0-preview`.
- Milestone log + research doc §7 update.

### Effort estimate

4 PRs, each ~1 focused session; largest is PR 3 (channel multiplex + SSE writer + client parser). Budget **2-2.5 days** focused work — comparable to v0.11.

### Non-goals for v0.12

- **Orleans streaming passthrough.** `OrleansAiAgentProxy` still doesn't proxy `StreamAsync`. 501 in v0.12; future pillar if someone asks.
- **WebSocket transport.** SSE only. WebSockets offer bi-directional but we only need server→client streaming.
- **Resume via `Last-Event-Id`.** Mid-stream disconnect = new turn. Consumers wanting resume use the v0.5 journal + a future Temporal-parity pillar.
- **Server-side replay / event-bus fan-out.** The endpoint is a single-caller consumer of the event bus for the duration of one run. Broader observability endpoints (e.g. cluster-wide event feed) are out of scope.
- **Streaming for tool-call outputs separate from agent text.** All text riding on `CompletionDelta` events; tool inputs/outputs are in `ToolCallStarted`/`Completed` event payloads.
- **OpenAPI spec emission for `text/event-stream` response type.** `.Produces<AgentEvent>(200, "text/event-stream")` is as much as we declare; the spec says content-type is SSE, schema is the AgentEvent hierarchy. Consumers doing codegen on the spec will need hand-authored SSE parsing — same as every other REST SSE API in the wild.

---

## Open items (for pillar planning, not blockers)

1. **`CompletionDelta.Context` field.** Matches `AgentEvent` base but feels heavy on every text chunk. Lean: keep for consistency; network serialisation is cheap at SSE scale.
2. **JSON property naming.** `camelCase` (matches `Web` defaults on the existing HTTP surface) vs. `PascalCase` (matches System.Text.Json raw default). Lean: camelCase — existing surface already uses `JsonSerializerDefaults.Web`.
3. **`AgentEvent` base's `Context` carries `AgentContext` with full metadata** (UserId, TenantId, CorrelationId). Potentially leaks caller identity back to caller — usually fine (they sent it) but worth noting.
4. **Heartbeat payload**. Pure SSE comment (`:`) vs. named event (`event: heartbeat\ndata: {"at":"..."}\n\n`). Lean: SSE comment — lower wire cost, comments-are-no-ops is a clean contract with the parser.
5. **Error-mid-stream shape.** Mid-stream handler exception: emit `event: turn.failed\ndata: {...}\n\n` and close the stream. Do NOT emit Problem Details mid-stream (stream is already committed, HTTP-level error handler can't set status).
6. **Client disconnect during heartbeat**. Channel writer sees broken pipe → linked CTS fires → all loops exit. Standard.
