# Guide: stream with tools

`StatefulAiAgent.StreamAsync` yields text deltas to the consumer as they arrive. When the model requests a tool mid-run, the outer loop dispatches, then re-enters the stream for the next turn. Consumer-visible surface stays `IAsyncEnumerable<string>` — tool observability flows through the event bus.

## The call

```csharp
await foreach (var delta in agent.StreamAsync("Summarise our Q1 sales, then email the summary."))
{
    Console.Write(delta);
}
```

That's it. The consumer sees text deltas for every streamed turn concatenated. Tool calls happen invisibly between streams.

## What's happening internally

```
TurnStarted
  → stream 1 opens: "Looking that up... " [deltas yielded to consumer]
  → stream 1 terminal update: ToolCalls = [{ lookup_sales }]
  → dispatch: ToolCallStarted → invoke → ToolCallCompleted
  → stream 2 opens: "Q1 sales totalled $4.2M. I'll email the summary now." [deltas yielded]
  → stream 2 terminal update: ToolCalls = [{ send_email }]
  → dispatch: ToolCallStarted → invoke → ToolCallCompleted
  → stream 3 opens: "Done — email sent to your inbox." [deltas yielded]
  → stream 3 terminal update: no ToolCalls
TurnCompleted
```

Consumer sees: `"Looking that up... Q1 sales totalled $4.2M. I'll email the summary now. Done — email sent to your inbox."` as deltas. Two tool calls dispatched. Session ends with one user turn + one final assistant turn.

## Observing tool dispatch

Subscribe to the event bus to see the tool rounds happening:

```csharp
using var sub = bus.Subscribe((@event, ct) =>
{
    if (@event is ToolCallStarted s)   Console.WriteLine($"[tool]  start  {s.ToolName} (call {s.CallId})");
    if (@event is ToolCallCompleted c) Console.WriteLine($"[tool]  end    {c.ToolName} ({(c.Succeeded ? "ok" : "failed")})");
    return ValueTask.CompletedTask;
});
```

The events interleave with the text stream roughly at the "ToolCalls terminal update" boundaries, but — since the event bus is best-effort async publish — their exact ordering relative to the consumer's `await foreach` iteration is not guaranteed. Treat events as a side channel for observability, not a strict synchronisation primitive.

## Budget applies

`RunBudget` enforces across all streamed turns:

```csharp
var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        ToolRegistry = registry,
        Budget = new RunBudget(
            MaxTurns: 5,           // ≤ 5 streamed turns in the run
            MaxToolCalls: 10,      // ≤ 10 tool dispatches total
            MaxDuration: TimeSpan.FromSeconds(30)),
    });

try
{
    await foreach (var d in agent.StreamAsync(input)) Console.Write(d);
}
catch (AgentBudgetExceededException ex)
{
    Console.Error.WriteLine($"\nBudget exceeded: {ex.BudgetField} (limit {ex.Limit}, observed {ex.Observed})");
}
```

Budget checks fire between streamed turns — an in-progress stream drains to whatever it had, then the next iteration's budget check trips + throws.

## Guardrails apply

Same three layers as non-streaming:

- **Input guardrails** fire before every streamed turn starts.
- **Output guardrails** fire **after** the stream drains on the final (non-tool-call) turn. Post-facto — deltas have already been yielded. If you need pre-emit gating, use `IStreamingAgentFilter.OnStreamDeltaAsync` to inspect each delta inline.
- **Tool guardrails** fire around each dispatch, same as non-streaming.

## Interrupts

An `IToolGuardrail` returning `GuardrailOutcome.Interrupt(payload)` mid-stream throws `AgentInterruptedException` out of `StreamAsync` — the enumerator ends. Resume with `agent.ResumeAsync(resumeInput)` (the v0.4 shim forwards the payload as a new turn; true mid-loop resume is durable-execution-pillar work).

```csharp
try
{
    await foreach (var d in agent.StreamAsync(input)) Console.Write(d);
}
catch (AgentInterruptedException ex)
{
    var decision = await PromptHumanAsync(ex.Interrupt);
    var resume = new ResumeInput(ex.Interrupt.InterruptId, JsonSerializer.SerializeToElement(decision));
    await agent.ResumeAsync(resume);   // note: ResumeAsync is non-streaming in v0.4
}
```

## What's bypassed on streaming (still, in v0.4.1)

- **`IAgentFilter`** (synchronous request/response chain) — doesn't apply on `StreamAsync`. Use `IStreamingAgentFilter` for streaming-aware hooks.
- **`ResiliencePipeline`** — not applied on streaming turns (wrapping `IAsyncEnumerable` retries has partial-output semantics issues).

Known gap; consumers needing filter-mediated behaviour (e.g. Langfuse enrichment via the legacy filter path) use `AskAsync`, or subscribe to the event bus and emit their own telemetry from there.

## Adapter specifics

Both SK and MAF adapters surface a terminal `CompletionUpdate.ToolCalls` on streamed turns that end with tool requests:

- **SK** uses SK's built-in `FunctionCallContentBuilder` to accumulate streaming `StreamingFunctionCallUpdateContent` fragments, then emits the terminal update post-drain.
- **MAF** walks each `AgentRunResponseUpdate.Contents` for `FunctionCallContent` items, deduplicates by `CallId`, then emits the terminal update post-drain.

The outer loop consumes the same `CompletionUpdate.ToolCalls` list either way.

## Things that catch people

- **Text + terminal tool-call is one turn.** The model that says "Looking that up..." then emits a tool call is producing one streamed turn, not two. The terminal `CompletionUpdate` carries `ToolCalls` alongside any trailing text.
- **`AgentBudgetExceededException.BudgetField`** is the field name (`"MaxTurns"` etc.), not a `LimitName` — easy to miss if you guessed from another framework.
- **Cache purge for package versions.** If you repack Vais.Agents preview versions locally, purge `~/.nuget/packages/vais.agents.*/0.4.0-preview/` (or `E:/nugets/vais.agents.*/0.4.0-preview/` on Windows depending on your `globalPackagesFolder` location) before rerunning — stale assemblies cause confusing build errors.

## See also

- [Execution loop concept](../concepts/execution-loop.md)
- [Tools concept](../concepts/tools.md)
- [Budget reference](../reference/budget.md)
- Sample: `samples/HelloStreamingTools/` (per samples plan)
