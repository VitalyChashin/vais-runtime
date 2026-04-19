// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;

// -----------------------------------------------------------------------------
// OrleansSilo — single-process silo + client. Memory providers (no external
// deps). Addresses an agent session by (agentId, sessionId), drives two turns
// through StatefulAiAgent, confirms the second turn sees the first turn's
// history via the silo-backed IAgentSession.
// -----------------------------------------------------------------------------

// Same-process silo+client: UseOrleans alone is enough; an IClusterClient is
// registered automatically when the silo boots.
var host = Host.CreateDefaultBuilder(args)
    .UseOrleans(builder =>
    {
        builder
            .UseLocalhostClustering()
            .AddMemoryGrainStorage("vais.agents")
            .AddMemoryGrainStorage("PubSubStore")
            .AddMemoryStreams("vais.agents.events");
    })
    .ConfigureServices(services =>
    {
        services.AddOrleansAgentRuntime();
        services.AddOrleansAgentEventBus();
        services.AddSingleton<ICompletionProvider, EchoingProvider>();
    })
    .Build();

await host.StartAsync();

// GetSession lives on OrleansAgentRuntime concretely; IAgentRuntime exposes GetOrCreate only.
var runtime = (OrleansAgentRuntime)host.Services.GetRequiredService<IAgentRuntime>();
var provider = host.Services.GetRequiredService<ICompletionProvider>();

// First turn: lands in the silo-backed session.
var session = runtime.GetSession(agentId: "support", sessionId: "conv-1");
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    AgentName = "support",
    Session = session,
});
Console.WriteLine($"[turn 1] reply: {await agent.AskAsync("Remember: my order id is ORD-42.")}");

// Second turn: same session → history hydrates from the silo.
var session2 = runtime.GetSession(agentId: "support", sessionId: "conv-1");
var agent2 = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    AgentName = "support",
    Session = session2,
});
Console.WriteLine($"[turn 2] reply: {await agent2.AskAsync("What's my order id?")}");

Console.WriteLine();
Console.WriteLine($"session.History.Count = {session2.History.Count} (user + assistant × 2)");

await host.StopAsync();

// ---- Echoing provider that references the history it sees ----
sealed class EchoingProvider : ICompletionProvider
{
    public string ProviderName => "echoing";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var text = $"received: \"{req.History[^1].Text}\". history-so-far={req.History.Count} turn(s)";
        return Task.FromResult(new CompletionResponse(text, ModelId: "echo"));
    }
}
