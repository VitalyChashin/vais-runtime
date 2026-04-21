# Reference: events

Two closed event hierarchies ship:

- **`AgentEvent`** — per-agent-run taxonomy. Eight sealed record subclasses deriving from `AgentEvent(At, Context)`. Published via `IAgentEventBus`.
- **`AgentGraphEvent`** — graph-scoped taxonomy shipped in v0.9. Nine sealed record subclasses deriving from `AgentGraphEvent(At, Context, RunId, SuperStep)`. Yielded by `IAgentGraph<TState>.StreamAsync` / `ResumeStreamAsync`. See [graph orchestration concept](../concepts/graph-orchestration.md).

## `AgentEvent`

Published via `IAgentEventBus`; subscribers see every event fanned out from every agent sharing the bus.

### Base

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

## Streaming (v0.12)

| Event | Raised | Fields |
|---|---|---|
| `CompletionDelta` | by `IStreamingAiAgent.StreamAsync` per streamed text chunk | `TextDelta`, `ModelId?`, `PromptTokens?`, `CompletionTokens?`, `ToolCalls?` |

`TextDelta` is non-null (may be empty on a terminal update that carries only metadata or tool-calls). Consumers aggregating a run sum deltas; take the final non-null `ModelId` / `PromptTokens` / `CompletionTokens` as authoritative. `ToolCalls` populates on the terminal pre-dispatch update when the model requests tool invocations — actual dispatch events (`ToolCallStarted` / `ToolCallCompleted`) follow separately on the bus.

`CompletionDelta` rides the SSE wire on the v0.12 `POST /v1/agents/{id}/invoke/stream` route — see [stream invocations over HTTP](../guides/stream-invocations-over-http.md).

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

// streaming (v0.12):
new CompletionDelta(DateTimeOffset.UtcNow, context, textDelta, modelId, promptTokens, completionTokens, toolCalls);
```

## SSE wire-event names (v0.12)

`POST /v1/agents/{id}/invoke/stream` serialises every `AgentEvent` subtype to a Server-Sent Events frame. The `event:` field carries a stable kebab-case name; the `data:` field carries JSON for the subclass body.

| `AgentEvent` subtype | SSE `event:` name |
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

Ten event names — one per closed-hierarchy subtype. Wire-name strings are stable contract; renaming is a breaking change requiring a major-version bump on the HTTP surface. See [ADR 0004](../adr/0004-sse-event-taxonomy-on-wire.md).

Heartbeat comments (`: heartbeat <utc>`) fire between events at `StreamingInvokeOptions.HeartbeatInterval` cadence (15s default). SSE parsers ignore comment lines — they keep proxies and load balancers from idling the connection.

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

## `AgentGraphEvent` (v0.9)

Graph-scoped taxonomy yielded by `IAgentGraph<TState>.StreamAsync` / `ResumeStreamAsync`. Every event carries `RunId` (matches the checkpointer's run key) and `SuperStep` (zero-based super-step index at emission time) so consumers can correlate against the checkpoint timeline.

### Base

```csharp
public abstract record AgentGraphEvent(
    DateTimeOffset At,
    AgentContext Context,
    string RunId,
    int SuperStep);
```

### Subtypes

| Event | Raised | Fields (beyond base) | Wire name (SSE) |
|---|---|---|---|
| `GraphStarted` | once, before the entry node runs | `GraphId`, `GraphVersion`, `EntryNodeId` | `graph.started` |
| `NodeStarted` | before each node executes | `NodeId`, `NodeKind` | `graph.node.started` |
| `NodeCompleted` | after each node succeeds, before outgoing edges evaluate | `NodeId`, `NodeKind`, `Duration` | `graph.node.completed` |
| `EdgeTraversed` | after predicate match + `OnTraverse` effect applied, before target node runs | `From`, `To` | `graph.edge.traversed` |
| `StateUpdated` | after a state-mutating effect (node binding or edge effect) | `ChangedKeys` | `graph.state.updated` |
| `GraphInterrupted` | when the graph hits an `Interrupt`-kind node (a checkpoint has been persisted) | `NodeId`, `InterruptId`, `Reason?` | `graph.interrupted` |
| `GraphResumed` | when a previously-interrupted run resumes from checkpoint | `ResumedFromNodeId`, `InterruptId` | `graph.resumed` |
| `GraphCompleted` | on `End`-node arrival or natural termination | `FinalNodeId`, `Duration` | `graph.completed` |
| `GraphFailed` | on unhandled exception, max-steps hit, or manifest error | `ErrorType`, `ErrorMessage`, `Duration` | `graph.failed` |

### Subclass constructor positions

Same pattern as `AgentEvent` — `At` + `Context` + `RunId` + `SuperStep` first, then subclass-specific fields:

```csharp
new GraphStarted(DateTimeOffset.UtcNow, context, runId, superStep: 0, graphId, graphVersion, entryNodeId);
new NodeStarted(DateTimeOffset.UtcNow, context, runId, superStep, nodeId, nodeKind);
new NodeCompleted(DateTimeOffset.UtcNow, context, runId, superStep, nodeId, nodeKind, duration);
new EdgeTraversed(DateTimeOffset.UtcNow, context, runId, superStep, from, to);
new StateUpdated(DateTimeOffset.UtcNow, context, runId, superStep, changedKeys);
new GraphInterrupted(DateTimeOffset.UtcNow, context, runId, superStep, nodeId, interruptId, reason);
new GraphResumed(DateTimeOffset.UtcNow, context, runId, superStep, resumedFromNodeId, interruptId);
new GraphCompleted(DateTimeOffset.UtcNow, context, runId, superStep, finalNodeId, duration);
new GraphFailed(DateTimeOffset.UtcNow, context, runId, superStep, errorType, errorMessage, duration);
```

Closed hierarchy — consumers pattern-match on subtype; adding a new subtype is an **unshipped** addition to `Vais.Agents.Abstractions`.

## See also

- [Observability concept](../concepts/observability.md)
- [Execution loop concept](../concepts/execution-loop.md) — where each event fires in the run.
- [Guardrails concept](../concepts/guardrails.md)
- [Orchestration concept](../concepts/orchestration.md)
- [Graph orchestration concept](../concepts/graph-orchestration.md) — where each `AgentGraphEvent` fires in the BSP super-step loop.
