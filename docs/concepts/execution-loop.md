# Execution loop

`StatefulAiAgent` owns the outer tool-call loop — the same loop for `AskAsync` and `StreamAsync`. SK and MAF adapters are "one-shot completion providers" (they return a response with text + optional tool calls); the agent drives the iteration.

## The loop

```mermaid
sequenceDiagram
    participant C as Caller
    participant A as StatefulAiAgent
    participant P as ICompletionProvider
    participant D as IToolCallDispatcher
    participant T as ITool

    C->>A: AskAsync(userMessage)
    A->>A: Session.AppendAsync(user turn)
    A->>A: snapshot → workingHistory
    loop per turn
        A->>A: budget check (MaxTurns, MaxDuration)
        A->>A: history reducer + context providers + packer
        A->>A: input guardrails
        A->>P: CompleteAsync(request)
        P-->>A: CompletionResponse(text, ToolCalls?)
        A->>A: token budget check
        alt ToolCalls is empty
            A->>A: output guardrails
            A->>A: break
        else has ToolCalls
            A->>A: append Assistant(text, ToolCalls) to workingHistory
            loop per tool call
                A->>D: DispatchAsync(call)
                D->>D: tool guardrails
                D->>T: InvokeAsync(args)
                T-->>D: result
                D->>A: ToolCallOutcome
                A->>A: append Tool(result, callId) to workingHistory
            end
        end
    end
    A->>A: Session.AppendAsync(final assistant turn)
    A-->>C: final assistant text
```

`StreamAsync` follows the same shape: each iteration streams `CompletionUpdate`s; a terminal update carrying `ToolCalls` kicks the tool-dispatch branch, then the next iteration opens a new stream. Consumer-visible `IAsyncEnumerable<string>` surface — text deltas across all turns, tool dispatches invisible to the consumer stream (but visible on the event bus).

## Core types

```csharp
namespace Vais.Agents;

public sealed record ToolCallRequest(string ToolName, JsonElement Arguments, string CallId);
public sealed record ToolCallOutcome(string CallId, string? Result, string? Error = null);

public interface IToolCallDispatcher
{
    ValueTask<ToolCallOutcome> DispatchAsync(ToolCallRequest request, AgentContext context, CancellationToken cancellationToken = default);
}

public sealed record RunBudget(
    int? MaxTurns = null,
    int? MaxToolCalls = null,
    int? MaxPromptTokens = null,
    int? MaxCompletionTokens = null,
    TimeSpan? MaxDuration = null)
{
    public static readonly RunBudget Unlimited = new();
}

public sealed class AgentBudgetExceededException : Exception
{
    public string BudgetField { get; }
    public object Limit { get; }
    public object Observed { get; }
}

public sealed record AgentInterrupt(string InterruptId, string Reason, JsonElement Payload);
public sealed record ResumeInput(string InterruptId, JsonElement Payload);
public sealed class AgentInterruptedException : Exception { public AgentInterrupt Interrupt { get; } }
```

## Working history vs session history

The loop maintains a **working history** list for the run — it starts as `Session.History.ToArray()` (which already includes the just-appended user turn) and grows with `ChatTurn.Assistant(text, ToolCalls: ...)` + `ChatTurn.Tool(result, ToolCallId: callId)` turns between iterations. The session itself is only mutated twice per run:

1. User turn appended at run entry.
2. Final assistant turn appended at run exit (only on success).

So `Session.History` is always clean alternating user / assistant turns, while intra-run tool-dispatch rounds live only in the ephemeral working history fed into each turn's `CompletionRequest`. This matches Bedrock AgentCore / OpenAI Assistants semantics and keeps `IAgentSession` persistence payloads small.

## RunBudget — where each field trips

| Field | Checked at | Behaviour |
|---|---|---|
| `MaxTurns` | top of each turn | throws `AgentBudgetExceededException("MaxTurns", ...)` |
| `MaxDuration` | top of each turn | throws `AgentBudgetExceededException("MaxDuration", ...)` |
| `MaxPromptTokens` | after provider returns | sum across all turns; throws on breach |
| `MaxCompletionTokens` | after provider returns | sum across all turns; throws on breach |
| `MaxToolCalls` | before each dispatch | counts every dispatch in the run; throws on breach |

All budgets count **across** tool-call iterations in the same run — not per-iteration. `RunBudget.Unlimited` is the default.

## Interrupts (HITL)

`AgentInterrupt` is raised when any guardrail layer returns `GuardrailOutcome.Interrupt(payload)`. The agent publishes `InterruptRaised` then throws `AgentInterruptedException(interrupt)`. The caller:

1. Catches `AgentInterruptedException`.
2. Inspects `ex.Interrupt.InterruptId`, `.Reason`, `.Payload`.
3. Gathers a human decision (or machine-automated approval in non-interactive flows).
4. Builds a `ResumeInput(interrupt.InterruptId, responsePayload)`.
5. Calls `agent.ResumeAsync(resumeInput)`.

v0.4's `ResumeAsync` is a **shim** — it forwards `resumeInput.Payload` as the next user turn through `AskAsync`. True mid-loop resume (picking up exactly where the interrupt paused, with working-history replay) ships with the durable-execution pillar. In v0.4 the interrupt-id correlation still flows through for observability; the behaviour is a new turn, not a continuation of the interrupted one. Documented explicitly.

## Wiring

```csharp
var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        ToolRegistry = registry,
        Budget = new RunBudget(MaxTurns: 5, MaxToolCalls: 10, MaxDuration: TimeSpan.FromSeconds(30)),
        ToolGuardrails = toolGuardrails,   // passed to DefaultToolCallDispatcher
        // ToolCallDispatcher = new MyCustomDispatcher(...),  // override the default
    });

try
{
    var reply = await agent.AskAsync("What's the weather in Paris? Then email a summary.");
}
catch (AgentInterruptedException ex)
{
    var decision = await PromptHumanAsync(ex.Interrupt);
    var resume = new ResumeInput(ex.Interrupt.InterruptId, JsonSerializer.SerializeToElement(decision));
    await agent.ResumeAsync(resume);
}
catch (AgentBudgetExceededException ex)
{
    Console.Error.WriteLine($"Budget {ex.BudgetField} exceeded: limit={ex.Limit}, observed={ex.Observed}");
}
```

## Tool-using streaming

`StatefulAiAgent.StreamAsync` uses the same outer-loop shape. `CompletionUpdate` gained `IReadOnlyList<ToolCallRequest>? ToolCalls` in v0.4.1 — providers emit a terminal `CompletionUpdate` with `ToolCalls` populated when the model wants to call tools, and the loop re-enters the stream after dispatch. Consumer surface stays `IAsyncEnumerable<string>` — tool observability flows through the event bus.

SK streaming uses SK's built-in `FunctionCallContentBuilder` to accumulate streamed `StreamingFunctionCallUpdateContent` fragments. MAF streaming walks `AgentRunResponseUpdate.Contents` for `FunctionCallContent` items, deduplicates by `CallId`.

See the [stream-with-tools guide](../guides/stream-with-tools.md) for the full recipe.

## Streaming filters (v0.10)

v0.4.1 shipped `StreamAsync` with a well-known gap: `IAgentFilter` didn't apply, and the `ResiliencePipeline` was bypassed. v0.10 closes both via `IStreamingAgentFilter` in `Vais.Agents.Abstractions` — one interface, three override points:

| Method | Fires | Default |
|---|---|---|
| `InvokeAsync(request, next, ct) : IAsyncEnumerable<CompletionUpdate>` | **Around** the provider. Rewrite the request, short-circuit the call, observe the full stream. Standard ASP.NET middleware shape — call `next` to continue, return your own enumerable to short-circuit. | Pass-through to `next`. |
| `OnStreamDeltaAsync(update, ct) : ValueTask<CompletionUpdate>` | **Per delta**. Transform each `CompletionUpdate` inline before it's yielded to the consumer — PII scrub, telemetry, pre-emit gating. | Return `update` unchanged. |
| `OnStreamCompleteAsync(final, ct) : ValueTask` | **Once**, end-of-stream, before output guardrails. Post-drain validation on the accumulated response. | No-op. |

Filters register via `StatefulAgentOptions.StreamingFilters` (an `IReadOnlyList<IStreamingAgentFilter>`, empty by default). Registration order determines execution order — the last filter in the list sits innermost, closest to the provider. Single filter can override any subset of the three methods.

```csharp
public sealed class TypingIndicatorFilter : IStreamingAgentFilter
{
    public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Pre-stream hook — emit "typing…" to a sideband.
        await BroadcastTypingAsync(request.AgentName, ct);
        await foreach (var delta in next(request, ct))
            yield return delta;
        await ClearTypingAsync(request.AgentName, ct);
    }
}

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    StreamingFilters = [new TypingIndicatorFilter(), new LangfuseStreamingFilter()],
});
```

The filter chain orchestration lives in `StatefulAiAgent.StreamEventsCoreAsync` — it wraps the provider bottom-up and fires the three hooks in registration order.

## Streaming resilience (v0.10)

Sibling to the existing `StatefulAgentOptions.ResiliencePipeline`, v0.10 adds `StreamingResiliencePipeline` — a Polly pipeline that wraps the provider-plus-filter-chain on `StreamAsync`. Two behavioural rules:

1. **Phase 1 retry.** Retries apply only **before** the first `CompletionUpdate` is yielded — the window covering enumerator-open + first `MoveNextAsync`. Connection refused, provider throws on request parse, transient `429` at request start → Polly retries per the configured policy.

2. **Phase 2 drain.** Once a delta has been yielded to the consumer, retries **stop**. Mid-stream failure surfaces to the caller without a retry — re-executing would double-emit already-committed text and inflate token counts. The enumerator's try/finally disposes the underlying stream on whatever happens next.

Filter-thrown exceptions (subtypes of `FilterAbortedException`) bypass retry entirely — the filter is doing deliberate policy work; retrying would paper over the signal.

```csharp
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    StreamingResiliencePipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(250),
            BackoffType = DelayBackoffType.Exponential,
        })
        .Build(),
});
```

## Streaming Invoke over HTTP (v0.12)

The HTTP control plane publishes `POST /v1/agents/{id}/invoke/stream` — a Server-Sent Events endpoint that carries the **full `AgentEvent` hierarchy** on the wire, not just text. Twelve wire-event names, one per `AgentEvent` subtype. Clients get two shapes:

- `InvokeStreamEventsAsync` → `IAsyncEnumerable<AgentEvent>` — full taxonomy.
- `InvokeStreamAsync` → `IAsyncEnumerable<string>` — text-only projection, filtered client-side to `CompletionDelta.TextDelta`.

`CompletionDelta : AgentEvent` in `Vais.Agents.Abstractions` is the text-carrying event. `IStreamingAiAgent` in the same assembly is the capability interface the route probes for — agents that don't implement it return `501 urn:vais-agents:streaming-not-supported`. `StatefulAiAgent` implements both `IAiAgent` and `IStreamingAiAgent` out of the box.

See the [stream-invocations-over-http guide](../guides/stream-invocations-over-http.md).

## Events

One `TurnStarted` at run entry + one `TurnCompleted` or `TurnFailed` at run exit — enveloping the whole multi-turn run. Per-tool events (`ToolCallStarted`, `ToolCallCompleted`) + `GuardrailTriggered` + `InterruptRaised` fire inside the loop. See [events reference](../reference/events.md).

## Extension points

- **`IToolCallDispatcher`** — inject a custom dispatcher for bespoke invocation semantics. Default `DefaultToolCallDispatcher` runs tool guardrails, invokes via `ITool.InvokeAsync`, catches exceptions into `ToolCallOutcome.Error`, emits events. Any replacement should preserve that envelope.
- **`IStreamingAgentFilter`** (v0.10) — around-provider DIM + per-delta transform + post-drain hook for `StreamAsync`. Three override points on one interface.
- **`IAgentFilter`** — the ordered `CompletionRequest → CompletionResponse` chain for `AskAsync`. Streaming has its own filter interface (v0.10) — the two don't share the chain.
- **`IStreamingAiAgent`** (v0.12) — capability interface for agents that expose `StreamAsync(string, AgentContext, CancellationToken) : IAsyncEnumerable<AgentEvent>`. Probed by the HTTP streaming route.

## Limitations / known gaps

- **`ResumeAsync` is a shim in v0.4** — true durable resume needs the durable-execution pillar.
- **Budget overruns raise exceptions mid-iteration.** No "graceful stop with partial result" yet.
- **Phase 2 drain — no retry after first streamed delta.** Polly's `StreamingResiliencePipeline` (v0.10) retries only before the first `CompletionUpdate` is yielded. Mid-stream failures rethrow without retry.
- **Orleans-silo streaming passthrough deferred.** v0.12 `POST /invoke/stream` returns `501 urn:vais-agents:streaming-not-supported` when the agent is Orleans-hosted without a direct `IStreamingAiAgent` implementation.

## See also

- [Architecture](architecture.md)
- [Tools](tools.md) — `ITool` + `Tool.FromFunc` + `IToolSource`.
- [Guardrails](guardrails.md) — the three layers the dispatcher + agent invoke.
- [Events reference](../reference/events.md) — `AgentEvent` closed hierarchy + v0.9 `AgentGraphEvent`.
- [Budget reference](../reference/budget.md) — `RunBudget` fields.
- [Stream invocations over HTTP](../guides/stream-invocations-over-http.md) — v0.12 HTTP streaming walkthrough.
