// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Persistence.Postgres;

// -----------------------------------------------------------------------------
// OrleansPostgresPersistence — silo backed by Postgres for clustering + grain
// storage. Postgres doesn't ship an Orleans streams provider in v0.4 so the
// event bus stays on memory streams here.
// Requires Docker + Orleans schema applied (see README).
// -----------------------------------------------------------------------------

var pgConn = Environment.GetEnvironmentVariable("POSTGRES_CONN")
    ?? "Host=localhost;Port=5432;Username=vais;Password=vais;Database=vais_agents";
Console.WriteLine($"Postgres: {pgConn}");

var host = Host.CreateDefaultBuilder(args)
    .UseOrleans(builder =>
    {
        builder
            .UseAgenticPostgresClustering(pgConn)
            .AddAgenticPostgresGrainStorage(pgConn)
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

var runtime = (OrleansAgentRuntime)host.Services.GetRequiredService<IAgentRuntime>();
var provider = host.Services.GetRequiredService<ICompletionProvider>();

var session = runtime.GetSession("support", "conv-pg");
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { AgentName = "support", Session = session });
Console.WriteLine($"[turn 1] {await agent.AskAsync("Postgres is backing this run.")}");

var session2 = runtime.GetSession("support", "conv-pg");
var agent2 = new StatefulAiAgent(provider, new StatefulAgentOptions { AgentName = "support", Session = session2 });
Console.WriteLine($"[turn 2] {await agent2.AskAsync("What did I just say?")}");

Console.WriteLine($"session.History.Count = {session2.History.Count}");
await host.StopAsync();

sealed class EchoingProvider : ICompletionProvider
{
    public string ProviderName => "echoing";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(
            $"received: \"{req.History[^1].Text}\" (history={req.History.Count})",
            ModelId: "echo"));
}
