// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// BudgetEnforcement — shows RunBudget tripping each of its dimensions:
// MaxToolCalls, MaxTurns, MaxCompletionTokens. The provider is scripted to
// keep requesting tools indefinitely, so each budget cap catches the runaway
// in a different way. Prints the BudgetField + Limit + Actual from each
// exception.
// -----------------------------------------------------------------------------

var registry = new SingletonRegistry(new PingTool());

async Task RunWithBudgetAsync(string label, RunBudget budget, ICompletionProvider provider)
{
    var agent = new StatefulAiAgent(
        provider,
        new StatefulAgentOptions { ToolRegistry = registry, Budget = budget });
    Console.WriteLine($"=== {label} ===");
    try
    {
        var reply = await agent.AskAsync("Keep pinging.");
        Console.WriteLine($"  reply: {reply}");
    }
    catch (AgentBudgetExceededException ex)
    {
        Console.WriteLine($"  BUDGET {ex.BudgetField} — limit={ex.Limit}, observed={ex.Observed}");
    }
    Console.WriteLine();
}

// MaxToolCalls=1 → trips on the second dispatch.
await RunWithBudgetAsync(
    "MaxToolCalls = 1",
    new RunBudget(MaxToolCalls: 1),
    new InfinitePingingProvider());

// MaxTurns=2 → trips at top of iteration 3.
await RunWithBudgetAsync(
    "MaxTurns = 2",
    new RunBudget(MaxTurns: 2),
    new InfinitePingingProvider());

// MaxCompletionTokens=10 — fake provider reports 8 tokens per turn; second
// turn's tally (16) breaches.
await RunWithBudgetAsync(
    "MaxCompletionTokens = 10",
    new RunBudget(MaxCompletionTokens: 10),
    new InfinitePingingProvider(completionTokensPerTurn: 8));

// Clean-path scenario for reference: generous budget, scripted to answer on turn 2.
await RunWithBudgetAsync(
    "Generous budget, scripted to settle on turn 2",
    new RunBudget(MaxTurns: 10, MaxToolCalls: 10),
    new ScriptedProviderSettlingOnTurn2());

// ---- Infinite-tool-calling provider ----
sealed class InfinitePingingProvider(int? completionTokensPerTurn = null) : ICompletionProvider
{
    public string ProviderName => "infinite-pinger";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var call = new ToolCallRequest(
            ToolName: "ping",
            Arguments: JsonDocument.Parse("{}").RootElement.Clone(),
            CallId: Guid.NewGuid().ToString("N"));
        return Task.FromResult(new CompletionResponse(
            Text: "",
            ModelId: "fake-model",
            PromptTokens: null,
            CompletionTokens: completionTokensPerTurn,
            ToolCalls: new[] { call }));
    }
}

// ---- Settles on turn 2 ----
sealed class ScriptedProviderSettlingOnTurn2 : ICompletionProvider
{
    private int _turn;
    public string ProviderName => "settling";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        _turn++;
        if (_turn == 1)
        {
            var call = new ToolCallRequest("ping", JsonDocument.Parse("{}").RootElement.Clone(), "call-1");
            return Task.FromResult(new CompletionResponse("", ModelId: "fake-model", ToolCalls: new[] { call }));
        }
        return Task.FromResult(new CompletionResponse("ok, pinged once.", ModelId: "fake-model"));
    }
}

sealed class PingTool : ITool
{
    public string Name => "ping";
    public string Description => "Echo a pong.";
    public JsonElement ParametersSchema { get; } =
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
    public Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
        => Task.FromResult("pong");
}

sealed class SingletonRegistry(ITool tool) : IToolRegistry
{
    public IReadOnlyList<ITool> Tools { get; } = new[] { tool };
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}
