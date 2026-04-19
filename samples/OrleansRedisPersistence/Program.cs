// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Persistence.Redis;

// -----------------------------------------------------------------------------
// OrleansRedisPersistence — single-process Orleans silo backed by Redis for
// clustering, grain storage, and event streams. Requires `docker compose up -d
// redis` (see docker-compose.yml). Drives two turns through the same
// (agentId, sessionId) to demonstrate state surviving across agent instances.
// -----------------------------------------------------------------------------

var redisConn = Environment.GetEnvironmentVariable("REDIS_CONN") ?? "localhost:6379";
Console.WriteLine($"Redis: {redisConn}");

var host = Host.CreateDefaultBuilder(args)
    .UseOrleans(builder =>
    {
        builder
            .UseAgenticRedisClustering(redisConn)
            .AddAgenticRedisGrainStorage(redisConn)
            .UseAgenticRedisStreaming(redisConn)
            .AddMemoryGrainStorage("PubSubStore");
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

var session = runtime.GetSession("support", "conv-redis");
var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { AgentName = "support", Session = session });
Console.WriteLine($"[turn 1] {await agent.AskAsync("Redis is backing this run.")}");

var session2 = runtime.GetSession("support", "conv-redis");
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
