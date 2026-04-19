# Hello agent

A 30-second walkthrough: stateful multi-turn chat, first on Semantic Kernel, then on Microsoft Agent Framework, using the same agent class. Source in [`samples/HelloAgent/`](../../samples/HelloAgent/).

## Prerequisites

- `.NET 9 SDK`.
- `OPENAI_API_KEY` in your environment.
- Packages installed per [installation](installation.md).

## The agent

`StatefulAiAgent` is the neutral agent class — it owns session history, runs each turn through an optional filter chain + a resilience pipeline, emits events + telemetry, and dispatches tool calls. The completion provider (SK or MAF) is injected; the agent itself is stack-agnostic.

```csharp
using Microsoft.SemanticKernel;
using Vais.Agents;
using Vais.Agents.Ai.SemanticKernel;
using Vais.Agents.Core;

var kernel = Kernel.CreateBuilder()
    .AddOpenAIChatCompletion("gpt-4o-mini", Environment.GetEnvironmentVariable("OPENAI_API_KEY")!)
    .Build();

var agent = new StatefulAiAgent(
    provider: new SkCompletionProvider(kernel),
    options: new StatefulAgentOptions { SystemPrompt = "Be concise." });

Console.WriteLine(await agent.AskAsync("What is the capital of France?"));
// → Paris.

Console.WriteLine(await agent.AskAsync("And its population?"));
// → About 2.1 million in the city proper (the agent kept context from turn 1).
```

## Swap to MAF — one `using` change

```csharp
using Microsoft.Extensions.AI;
using Vais.Agents.Ai.MicrosoftAgentFramework;

IChatClient client = /* your MEAI IChatClient, e.g. new OpenAIClient(...).AsChatClient("gpt-4o-mini") */;
var agent = new StatefulAiAgent(
    provider: new MafCompletionProvider(client),
    options: new StatefulAgentOptions { SystemPrompt = "Be concise." });
```

Same `StatefulAiAgent` class, same options shape, same `AskAsync` call. The agent's outer loop, history management, tool dispatch, budget enforcement, events — none of it changes.

## Add a tool

Tools are `ITool` instances surfaced via an `IToolRegistry`. The agent's outer loop dispatches them through `IToolCallDispatcher` when the model requests them.

```csharp
using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Core;

sealed class RollDiceTool : ITool {
    public string Name => "roll_dice";
    public string Description => "Roll an N-sided die; N defaults to 6.";
    public JsonElement ParametersSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"sides":{"type":"integer"}}}""").RootElement;

    public Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default) {
        var sides = args.TryGetProperty("sides", out var s) ? s.GetInt32() : 6;
        return Task.FromResult(Random.Shared.Next(1, sides + 1).ToString());
    }
}

sealed class SingletonRegistry(ITool tool) : IToolRegistry {
    public IReadOnlyList<ITool> Tools { get; } = new[] { tool };
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}

var agent = new StatefulAiAgent(
    new SkCompletionProvider(kernel),
    new StatefulAgentOptions { ToolRegistry = new SingletonRegistry(new RollDiceTool()) });

Console.WriteLine(await agent.AskAsync("Roll a 20-sided die for me."));
```

The model requests `roll_dice({sides: 20})`, the outer loop dispatches it, appends the result to working history, and loops again for the final answer. History (`agent.History`) ends up with just the user turn + final assistant turn — intermediate tool-call rounds live in the run's working history only.

## What ran

By default the agent emits:

- `gen_ai.*` OpenTelemetry activity + metrics (per [ADR 0002](../adr/0002-otel-genai-conventions.md)).
- `AgentEvent` stream: `TurnStarted`, `ToolCallStarted`, `ToolCallCompleted`, `TurnCompleted` — `null` event bus by default, wire one up to observe.
- A `UsageRecord` per turn via `IUsageSink` — `NullUsageSink` by default; wire `OpenTelemetryUsageSink` for metrics.

None of these cost anything if you haven't attached a listener / sink.

## Next

- [Choosing a stack](choosing-a-stack.md) — tradeoffs between SK and MAF.
- [Execution loop concept](../concepts/execution-loop.md) — what happens inside `AskAsync` / `StreamAsync`.
- [Tools concept](../concepts/tools.md) — `ITool`, registry, `Tool.FromFunc<TIn,TOut>`.
- [Wire a custom tool guide](../guides/wire-a-custom-tool.md) — deeper recipe.
