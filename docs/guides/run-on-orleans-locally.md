# Guide: run on Orleans locally

Stand up a single-process Orleans silo + client in the same `Main`, address agents by `(agentId, sessionId)`, drive one turn. No Docker, no Redis, no Postgres — memory providers are enough for the local walkthrough. Later guides swap in Redis + Postgres for real persistence.

## Project setup

```
MyAgentHost.csproj
```

References:

```xml
<PackageReference Include="Vais.Agents.Abstractions" Version="0.4.0-preview" />
<PackageReference Include="Vais.Agents.Core" Version="0.4.0-preview" />
<PackageReference Include="Vais.Agents.Ai.SemanticKernel" Version="0.4.0-preview" />
<PackageReference Include="Vais.Agents.Hosting.Orleans" Version="0.4.0-preview" />
<PackageReference Include="Microsoft.Orleans.Server" Version="10.1.0" />
<PackageReference Include="Microsoft.Orleans.Client" Version="10.1.0" />
```

## `Program.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.SemanticKernel;
using Orleans.Hosting;
using Vais.Agents;
using Vais.Agents.Ai.SemanticKernel;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;

// --- silo (server side) ------------------------------------------------------
var silo = Host.CreateDefaultBuilder(args)
    .UseOrleans(builder =>
    {
        builder
            .UseLocalhostClustering()
            .AddMemoryGrainStorage("vais.agents")          // session / config grain state
            .AddMemoryGrainStorage("PubSubStore")          // required by memory streams
            .AddMemoryStreams("vais.agents.events");       // OrleansAgentEventBus transport
    })
    .Build();

await silo.StartAsync();

// --- client (in the same process here; separate in production) --------------
var client = Host.CreateDefaultBuilder(args)
    .UseOrleansClient(builder =>
    {
        builder
            .UseLocalhostClustering()
            .AddMemoryStreams("vais.agents.events");       // mirror silo config
    })
    .ConfigureServices(services =>
    {
        services.AddOrleansAgentRuntime();

        // Completion provider — same DI wiring as a single-process app.
        services.AddSingleton(_ =>
        {
            var kernel = Kernel.CreateBuilder()
                .AddOpenAIChatCompletion("gpt-4o-mini", Environment.GetEnvironmentVariable("OPENAI_API_KEY")!)
                .Build();
            return new SkCompletionProvider(kernel);
        });
    })
    .Build();

await client.StartAsync();

// --- drive a turn -----------------------------------------------------------
var runtime = client.Services.GetRequiredService<IAgentRuntime>();
var provider = client.Services.GetRequiredService<SkCompletionProvider>();

var session = runtime.GetSession(agentId: "support-agent", sessionId: "conv-1");

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        Session = session,
        SystemPrompt = "Be concise.",
    });

Console.WriteLine(await agent.AskAsync("Say hello."));
Console.WriteLine(await agent.AskAsync("What did I just ask?"));   // context survives — state is in the silo

await client.StopAsync();
await silo.StopAsync();
```

## What's happening

1. **Silo** runs the grains. `IAgentSessionGrain` instances are virtual actors keyed by `{agentId}/{sessionId}`; state persists to the `"vais.agents"` grain storage provider (memory here, Redis / Postgres in follow-up guides).
2. **Client** holds an `IAgentRuntime` that exposes `GetSession(agentId, sessionId)` → `OrleansAgentSession`. That session is the durable state handle.
3. **`StatefulAiAgent` lives on the client** — the LLM turn loop runs here. Only the session's `History` crosses the Orleans RPC boundary.

Why client-side loop? Each caller pays its own LLM latency, silo stays cheap + deterministic, mid-stream tool dispatches don't need to marshal `IAsyncEnumerable` across grain calls. Matches Bedrock AgentCore / OpenAI Assistants semantics.

## Event bus from client side

```csharp
services.AddOrleansAgentEventBus();  // wires IAgentEventBus → OrleansAgentEventBus over the "vais.agents.events" stream

// In your handler:
var bus = client.Services.GetRequiredService<IAgentEventBus>();
using var sub = bus.Subscribe((@event, ct) =>
{
    Console.WriteLine($"[bus] {@event.GetType().Name} @ {@event.At:O}");
    return ValueTask.CompletedTask;
});

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { Session = session, EventBus = bus });
```

Events fan out across every subscribed client — if you run the silo on one machine and the subscriber on another, both see events via Orleans streams.

## Configuring per-agent options

The silo calls `ConfigureAgentGrains((sp, grainKey) => ...)` to build `StatefulAgentOptions` per-grain at activation time. For the local walkthrough above, it's not needed because the client owns the `StatefulAiAgent`. It **is** needed if you use the legacy silo-local `IAiAgentGrain` (runs the turn loop inside the silo) — see the `Hosting.Orleans.Tests` project for examples.

## Swap in Redis or Postgres

Same `Program.cs`, swap grain-storage provider:

```csharp
// Redis:
builder.UseAgenticRedisClustering(redisConn)
       .AddAgenticRedisGrainStorage(redisConn)
       .UseAgenticRedisStreaming(redisConn);

// Postgres:
builder.UseAgenticPostgresClustering(pgConn)
       .AddAgenticPostgresGrainStorage(pgConn);
```

See [add Redis persistence](add-redis-persistence.md) and [add Postgres persistence](add-postgres-persistence.md).

## Things that catch people

- **`ConfigureAwait(false)` in grain code** — Orleans 10's `ORLEANS0014` analyzer forbids it. Use `ConfigureAwaitOptions.ContinueOnCapturedContext` or omit.
- **Memory streams need `PubSubStore`** — `AddMemoryStreams` requires a grain storage provider named exactly `"PubSubStore"`. The silo builder above adds it; omitting causes a runtime error on first subscribe.
- **Client-side streams too.** `AddMemoryStreams("vais.agents.events")` must be on both silo *and* client builders — otherwise `GetStreamProvider("vais.agents.events")` returns null on the consumer side.

## See also

- [Session + memory concept](../concepts/session.md)
- [Persistence concept](../concepts/persistence.md)
- Sample: `samples/OrleansSilo/` (per samples plan)
