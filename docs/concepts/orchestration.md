# Orchestration

Multi-agent orchestration — pipelining, shared-history group chats, handoffs — sits below `IAiAgent`. Orchestrators drive `ICompletionProvider` directly via `AgentParticipant` records instead of composing `IAiAgent` instances, because multi-agent shared history and per-agent history are semantically incompatible.

## Why below IAiAgent

`IAiAgent` owns a single session history. A multi-agent conversation has a shared view every participant reads before its turn. Mixing the two either fragments the shared view (each participant has a truncated take) or pollutes each agent's private history with turns it didn't author. Dropping to the provider layer — `AgentParticipant(Name, Provider, SystemPrompt?)` — avoids both. The orchestrator owns the shared view; each participant is stateless relative to it.

Consumers who want `StatefulAiAgent`-style memory either use `StatefulAiAgent.AskAsync` for single-agent, or wrap an orchestrator output in their own history policy.

## Core types

```csharp
namespace Vais.Agents;

public sealed record AgentParticipant(string Name, ICompletionProvider Provider, string? SystemPrompt = null);

public sealed record OrchestrationStep(string AgentName, string Text, AgentChatRole Role = AgentChatRole.Assistant);

public interface IAgentOrchestrator
{
    IAsyncEnumerable<OrchestrationStep> RunAsync(string task, CancellationToken cancellationToken = default);
}

public interface ITerminationCondition
{
    Task<bool> ShouldTerminateAsync(IReadOnlyList<OrchestrationStep> steps, CancellationToken cancellationToken = default);
}

public sealed record Handoff(string FromAgent, string ToAgent, string? Message = null, IReadOnlyList<ChatTurn>? HistoryToCarry = null);
```

## Built-in orchestrators (in Core)

**`SequentialOrchestrator(IReadOnlyList<AgentParticipant>)`** — pipeline; each participant receives the previous participant's assistant text as its user message. One pass through the list.

```csharp
using Vais.Agents.Core;

var pipeline = new SequentialOrchestrator(new[]
{
    new AgentParticipant("researcher", researchProvider, SystemPrompt: "Gather facts. Be thorough."),
    new AgentParticipant("writer",     writerProvider,   SystemPrompt: "Turn facts into a one-paragraph summary."),
    new AgentParticipant("editor",     editorProvider,   SystemPrompt: "Polish for tone and clarity."),
});

await foreach (var step in pipeline.RunAsync("Summarise the Apollo programme."))
{
    Console.WriteLine($"[{step.AgentName}] {step.Text}");
}
```

**`RoundRobinOrchestrator(participants, maxRounds, ITerminationCondition?)`** — rotates through participants for up to `maxRounds` cycles. Each participant sees the full shared conversation (user task + all prior steps) as `CompletionRequest.History`, with prior steps encoded as assistant turns of the form `"[AgentName] text"`. Termination predicate evaluates after every yielded step.

```csharp
var debate = new RoundRobinOrchestrator(
    new[]
    {
        new AgentParticipant("optimist",  optimistProvider),
        new AgentParticipant("pessimist", pessimistProvider),
    },
    maxRounds: 3,
    termination: TerminationConditions.FromPredicate(steps =>
        steps.Any(s => s.Text.Contains("we agree", StringComparison.OrdinalIgnoreCase))));

await foreach (var step in debate.RunAsync("Should we ship on Friday?"))
    Console.WriteLine($"[{step.AgentName}] {step.Text}");
```

## `ITerminationCondition` — async and composable

Preferred over the older `TerminationPredicate` delegate — it's async (good for external policy checks) and composable via custom implementations. `TerminationConditions.FromPredicate` in Core bridges legacy predicate-based usage.

```csharp
sealed class LengthLimitTermination(int maxSteps) : ITerminationCondition
{
    public Task<bool> ShouldTerminateAsync(IReadOnlyList<OrchestrationStep> steps, CancellationToken ct)
        => Task.FromResult(steps.Count >= maxSteps);
}
```

Compose by implementing one class that combines multiple — AND / OR is the consumer's job.

## `Handoff` record

Explicit handoff from one agent to another. The record carries:

- `FromAgent` / `ToAgent` names.
- `Message` — optional string the target agent receives as its user input.
- `HistoryToCarry` — optional list of `ChatTurn`s to seed the target with.

`Handoff` is a data contract for consumer orchestration patterns + for the `HandoffRequested` event. v0.4 does not ship a built-in `HandoffOrchestrator` — the shape is explicit, and consumers compose handoffs with their own logic (detect handoff intent via guardrail / output inspection, seed the next agent via a new `StatefulAiAgent` or another orchestrator run).

### Handoff event

```csharp
AgentEvent handoffEvt = new HandoffRequested(DateTimeOffset.UtcNow, context, handoff);
```

Published by consumer code when a handoff occurs. The event bus lets downstream systems (a dashboard, a routing controller) observe the chain. `Handoff.HistoryToCarry` is deliberately excluded from Orleans surrogate serialisation — too easy to leak large payloads onto the event stream. Serialise only the identity fields (`FromAgent`, `ToAgent`, `Message`).

## Wiring into `IAgentEventBus`

Orchestrators in v0.4 do **not** emit agent events automatically. Consumers wrap participant providers with a custom `IAgentFilter` if they want per-step telemetry, or publish their own events around the orchestrator's `RunAsync` call. Keeping the two surfaces independent avoids baking orchestration into Core's event bus until a consumer asks for it.

## Extension points

- **Custom `IAgentOrchestrator`** — implement `RunAsync`, yield `OrchestrationStep`s. Any iteration policy.
- **Custom `ITerminationCondition`** — async, composable.
- **Framework-native wrappers** — a `SkGroupChatOrchestrator` wrapping `AgentGroupChat`, or a `MafOrchestrator` wrapping MAF's group-chat primitives. Not shipped; the two built-ins work across any provider.

## Limitations / known gaps

- **`IHandoff` interface skipped.** The `Handoff` record is the data contract; an interface would duplicate the surface without adding value.
- **`IAgentGraphExecutor` / `IAgentGraphBuilder` deferred.** Interface design is speculative without an implementation — the eventual `GraphOrchestrator` will shape its own contract. Until then, Sequential + RoundRobin + consumer-authored orchestrators cover the surveyed cases.
- **No LLM-driven next-speaker selection.** The built-ins cycle deterministically. A `SelectorOrchestrator` that delegates next-speaker choice to an LLM is a reasonable future add; punt until demand.
- **No `StatefulAiAgent`-as-participant wrapper.** Per the "below `IAiAgent`" rule. Consumers who want per-agent history do it above the orchestrator.

## See also

- [Architecture](architecture.md)
- [Events reference](../reference/events.md) — `HandoffRequested`.
- [Execution loop](execution-loop.md) — `ICompletionProvider` contract.
