// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using OpenAI;
using Vais2.Agents;
using Vais2.Agents.Ai.MicrosoftAgentFramework;
using Vais2.Agents.Ai.SemanticKernel;
using Vais2.Agents.Core;

// -----------------------------------------------------------------------------
// HelloAgent
//
// Runs the same Vais2.Agents StatefulAiAgent twice — first with the Semantic
// Kernel adapter, then with the Microsoft Agent Framework adapter — and prints
// both conversations side by side. The point is not that the two outputs match
// word-for-word (they won't, because sampling is non-deterministic), but that
// one stack-neutral agent class drives both stacks without change.
//
// Then a third segment runs the same tool-calling scenario through both stacks,
// using a trivial ITool + IToolRegistry to dog-food the M2c surface end-to-end.
// -----------------------------------------------------------------------------

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine(
        "OPENAI_API_KEY is not set. Set it and rerun, e.g.\n" +
        "  OPENAI_API_KEY=... dotnet run --project samples/HelloAgent");
    return 1;
}

var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";
const string systemPrompt = "You are a concise assistant. Answer in one short sentence.";

Console.WriteLine($"Model: {model}\n");

await RunConversationAsync("SK", BuildSkProvider(apiKey, model), systemPrompt);
Console.WriteLine();
await RunConversationAsync("MAF", BuildMafProvider(apiKey, model), systemPrompt);

Console.WriteLine();
await RunToolCallingAsync("SK tool-calling", BuildSkProvider(apiKey, model));
Console.WriteLine();
await RunToolCallingAsync("MAF tool-calling", BuildMafProvider(apiKey, model));
return 0;

static ICompletionProvider BuildSkProvider(string apiKey, string model)
{
    var kernel = Kernel.CreateBuilder()
        .AddOpenAIChatCompletion(modelId: model, apiKey: apiKey)
        .Build();
    return new SkCompletionProvider(kernel);
}

static ICompletionProvider BuildMafProvider(string apiKey, string model)
{
    var openAiClient = new OpenAIClient(apiKey);
    IChatClient chatClient = openAiClient.GetChatClient(model).AsIChatClient();
    return new MafCompletionProvider(chatClient, modelId: model);
}

static async Task RunConversationAsync(string label, ICompletionProvider provider, string systemPrompt)
{
    Console.WriteLine($"========== {label} ({provider.ProviderName}) ==========");
    var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { SystemPrompt = systemPrompt });

    var turn1 = await agent.AskAsync("What is the capital of France?");
    Console.WriteLine("user:      What is the capital of France?");
    Console.WriteLine($"assistant: {turn1}");

    var turn2 = await agent.AskAsync("And what river runs through it?");
    Console.WriteLine("user:      And what river runs through it?");
    Console.WriteLine($"assistant: {turn2}");

    Console.WriteLine($"[history size: {agent.History.Count} turns]");
}

static async Task RunToolCallingAsync(string label, ICompletionProvider provider)
{
    Console.WriteLine($"========== {label} ({provider.ProviderName}) ==========");

    var diceTool = new RollDiceTool();
    var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
    {
        SystemPrompt = "You are a helpful assistant. When the user asks for a dice roll, call the roll_dice tool and report the result.",
        ToolRegistry = new InMemoryToolRegistry(diceTool),
    });

    var reply = await agent.AskAsync("Roll a die for me and tell me what you got.");
    Console.WriteLine("user:      Roll a die for me and tell me what you got.");
    Console.WriteLine($"assistant: {reply}");
    Console.WriteLine($"[tool invocations: {diceTool.Invocations}, history size: {agent.History.Count} turns]");
}

/// <summary>A trivial parameterless tool that returns a random 1–6 dice roll as JSON.</summary>
internal sealed class RollDiceTool : ITool
{
    private int _invocations;

    public string Name => "roll_dice";

    public string Description => "Roll a standard 6-sided die. Takes no arguments. Returns the roll as JSON.";

    public JsonElement ParametersSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        required = Array.Empty<string>(),
    });

    public int Invocations => _invocations;

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocations);
        var roll = Random.Shared.Next(1, 7);
        return Task.FromResult($"{{\"result\":{roll}}}");
    }
}

/// <summary>
/// Minimal in-memory <see cref="IToolRegistry"/> — ships as part of this sample to dog-food how
/// much code a consumer has to write to wire up <see cref="StatefulAgentOptions.ToolRegistry"/>.
/// </summary>
internal sealed class InMemoryToolRegistry : IToolRegistry
{
    public InMemoryToolRegistry(params ITool[] tools) => Tools = tools;

    public IReadOnlyList<ITool> Tools { get; }

    public ITool? GetByName(string name)
    {
        foreach (var tool in Tools)
        {
            if (string.Equals(tool.Name, name, StringComparison.Ordinal))
            {
                return tool;
            }
        }
        return null;
    }
}
