// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

// -----------------------------------------------------------------------------
// HelloStreamingTools — the v0.4.1 tool-using streaming path. A scripted
// streaming provider emits:
//   turn 1: "Looking that up... " + terminal CompletionUpdate with ToolCalls
//   turn 2: "The forecast in Paris is mild and sunny." + no tool calls
// The outer loop dispatches the tool, re-enters the stream, the consumer sees
// one continuous IAsyncEnumerable<string> of deltas.
// -----------------------------------------------------------------------------

var weatherTool = new WeatherTool();
var registry = new SingletonRegistry(weatherTool);

var turn1 = new CompletionUpdate[]
{
    new("Looking that up... "),
    new(TextDelta: "", ToolCalls: new[]
    {
        new ToolCallRequest("get_weather",
            JsonDocument.Parse("""{"city":"Paris"}""").RootElement.Clone(),
            CallId: "call-1"),
    }),
};
var turn2 = new CompletionUpdate[]
{
    new("The forecast in "),
    new("Paris is mild "),
    new("and sunny."),
};
var provider = new ScriptedMultiTurnStreamingProvider(turn1, turn2);

var bus = new InMemoryAgentEventBus();
using var sub = bus.Subscribe((@event, ct) =>
{
    Console.WriteLine($"  [event] {@event.GetType().Name}");
    return ValueTask.CompletedTask;
});

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions { ToolRegistry = registry, EventBus = bus });

Console.Write("stream: ");
await foreach (var delta in agent.StreamAsync("What's the weather in Paris?"))
{
    Console.Write(delta);
    await Task.Delay(20);
}
Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"tool invocations: {weatherTool.Invocations}");
Console.WriteLine($"session turns: {agent.History.Count} (user + final assistant)");

// ---- Tool ----
sealed class WeatherTool : ITool
{
    public int Invocations { get; private set; }
    public string Name => "get_weather";
    public string Description => "Return the weather for a city.";
    public JsonElement ParametersSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""")
        .RootElement.Clone();
    public Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        Invocations++;
        var city = args.GetProperty("city").GetString();
        return Task.FromResult($"{{\"city\":\"{city}\",\"temp\":72,\"unit\":\"F\"}}");
    }
}

// ---- Multi-turn streaming provider ----
sealed class ScriptedMultiTurnStreamingProvider(params IEnumerable<CompletionUpdate>[] scripts)
    : ICompletionProvider, IStreamingCompletionProvider
{
    private readonly Queue<IEnumerable<CompletionUpdate>> _queue = new(scripts);
    public string ProviderName => "scripted-multi-turn";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => throw new NotSupportedException("streaming only");

#pragma warning disable CS1998
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_queue.TryDequeue(out var script))
            throw new InvalidOperationException("ran out of scripted streams");
        foreach (var u in script) yield return u;
    }
#pragma warning restore CS1998
}

sealed class SingletonRegistry(ITool tool) : IToolRegistry
{
    public IReadOnlyList<ITool> Tools { get; } = new[] { tool };
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}
