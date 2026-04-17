// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
