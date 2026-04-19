# Reference: events

`AgentEvent` is a closed hierarchy — eight sealed record subclasses, all derived from an abstract `AgentEvent(At, Context)` base. Published via `IAgentEventBus`; subscribers see every event fanned out from every agent sharing the bus.

## Base

```csharp
public abstract record AgentEvent(DateTimeOffset At, AgentContext Context);
```

Every event carries:
- `At` — UTC timestamp of when the event was raised.
- `Context` — a snapshot of `AgentContext` (user / tenant / correlation / agent name) at raise-time.

## Per-run envelope

Fires once per `AskAsync` / `StreamAsync` call — envelopes the whole run including multi-turn tool dispatches.

| Event | Raised | Fields |
|---|---|---|
| `TurnStarted` | at run entry, after user turn appended | `UserMessage` |
| `TurnCompleted` | at successful run exit, after final assistant turn appended | `AssistantText`, `ModelId?`, `PromptTokens?`, `CompletionTokens?`, `Duration` |
| `TurnFailed` | at failed run exit, instead of `TurnCompleted` | `ErrorType`, `ErrorMessage`, `Duration` |

Token counts on `TurnCompleted` are **summed across all streamed turns** inside the run.

## Tool dispatch

Fires per tool call; multiple per run when the agent makes several dispatches.

| Event | Raised | Fields |
|---|---|---|
| `ToolCallStarted` | by `DefaultToolCallDispatcher` before `ITool.InvokeAsync` | `CallId`, `ToolName` |
| `ToolCallCompleted` | by `DefaultToolCallDispatcher` after `ITool.InvokeAsync` | `CallId`, `ToolName`, `Succeeded`, `Error?`, `Duration` |

`ToolCallCompleted.Succeeded = false` covers both thrown exceptions and tool-level errors (e.g. MCP `IsError = true`).

## Guardrail + interrupt

| Event | Raised | Fields |
|---|---|---|
| `GuardrailTriggered` | when any guardrail returns Deny or Interrupt | `Layer`, `Decision`, `Reason?` |
| `InterruptRaised` | when any guardrail returns Interrupt (fires alongside `GuardrailTriggered`) | `InterruptId`, `Reason` |

Deny path: `GuardrailTriggered` → `TurnFailed`.
Interrupt path: `GuardrailTriggered` + `InterruptRaised` → `AgentInterruptedException`.

## Orchestration

| Event | Raised | Fields |
|---|---|---|
| `HandoffRequested` | by consumer code during a multi-agent handoff | `Handoff` |

Vais.Agents' built-in orchestrators don't automatically publish this — consumers emit it when a handoff occurs. Orleans surrogate excludes `Handoff.HistoryToCarry` from serialisation to avoid leaking large payloads onto the event stream.

## Subclass constructor positions (quick ref)

All subclasses put `At` + `Context` first, then the subclass-specific fields:

```csharp
// run envelope:
new TurnStarted(DateTimeOffset.UtcNow, context, userMessage);
new TurnCompleted(DateTimeOffset.UtcNow, context, assistantText, modelId, promptTokens, completionTokens, duration);
new TurnFailed(DateTimeOffset.UtcNow, context, errorType, errorMessage, duration);

// tool dispatch (note CallId before ToolName):
new ToolCallStarted(DateTimeOffset.UtcNow, context, callId, toolName);
new ToolCallCompleted(DateTimeOffset.UtcNow, context, callId, toolName, succeeded, error, duration);

// guardrails + interrupts:
new GuardrailTriggered(DateTimeOffset.UtcNow, context, layer, decision, reason);
new InterruptRaised(DateTimeOffset.UtcNow, context, interruptId, reason);

// orchestration:
new HandoffRequested(DateTimeOffset.UtcNow, context, handoff);
```

## `IAgentEventBus`

```csharp
public interface IAgentEventBus
{
    ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default);
    IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler);
}
```

Implementations:

- `NullAgentEventBus.Instance` (in `Vais.Agents.Core`) — no-op default.
- `InMemoryAgentEventBus` (in `Vais.Agents.Hosting.InMemory`) — `ImmutableArray<T>`-backed handler list; publish-snapshot-then-invoke so subscribe / unsubscribe from inside a handler is safe.
- `OrleansAgentEventBus` (in `Vais.Agents.Hosting.Orleans`) — publishes to the Orleans stream provider named `"vais.agents.events"`. Works with memory streams, Redis streams (`UseAgenticRedisStreaming`), or Event Hubs streams.

**Subscribers that throw are logged + swallowed.** The bus never breaks the publisher.

## Orleans surrogate mapping

Serialisation uses a single flat `AgentEventSurrogate` struct with an `AgentEventKind` discriminator. Per-subclass converter classes (`TurnStartedSurrogateConverter`, `ToolCallStartedSurrogateConverter`, etc.) all share helpers via an internal static — Orleans dispatches by exact runtime type, not polymorphic-by-base, so every subclass needs its own `[RegisterConverter]` entry.

Consumers don't see this — the surrogates + kind enum live inside `Vais.Agents.Hosting.Orleans`.

## See also

- [Observability concept](../concepts/observability.md)
- [Execution loop concept](../concepts/execution-loop.md) — where each event fires in the run.
- [Guardrails concept](../concepts/guardrails.md)
- [Orchestration concept](../concepts/orchestration.md)
