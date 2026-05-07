// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// HttpStreamingCancellation — cancel an in-flight SSE stream cleanly.
//
// Run: dotnet run --project samples/HttpStreamingCancellation
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/guides/stream-invocations-over-http.md
//
// A slow provider emits 30 deltas with a 20ms gap between each (600ms total).
// A CancellationTokenSource fires after 350ms; after HTTP setup the client
// typically sees ~10–15 deltas before the OperationCanceledException propagates.
//
// Server-side: HttpContext.RequestAborted fires when the connection closes;
// the streaming loop observes it and stops yielding new SSE frames.

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

// ---- server ----
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:0");

var registry  = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(new SlowStreamingProvider());
var lifecycle = new AgentLifecycleManager(registry, runtime);

await lifecycle.CreateAsync(
    new AgentManifest(
        Id:          "slow",
        Version:     "1.0",
        Handler:     new AgentHandlerRef("declarative"),
        Protocols:   [],
        Tools:       [],
        Description: "Slow-yielding scripted agent — 30 deltas × 30ms."),
    CancellationToken.None);

builder.Services.AddSingleton<IAgentRegistry>(registry);
builder.Services.AddSingleton<IAgentRuntime>(runtime);
builder.Services.AddSingleton<IAgentLifecycleManager>(lifecycle);
builder.Services.AddAgentControlPlane();

var app = builder.Build();
app.MapAgentControlPlane("/v1");

await app.StartAsync();
var serverUrl = app.Urls.First();
Console.WriteLine($"Server: {serverUrl}");
Console.WriteLine();

// ---- client: cancel after 200ms ----
var http = new HttpClient { BaseAddress = new Uri(serverUrl) };
var client = new AgentControlPlaneClient(http);

using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));

var request = new AgentInvocationRequest(Text: "Count slowly.");

Console.WriteLine("== streaming (will cancel after ~350ms) ==");
var received = 0;
try
{
    await foreach (var evt in client.InvokeStreamEventsAsync(
        "slow", request, version: null, idempotencyKey: null, cts.Token))
    {
        if (evt is CompletionDelta d)
        {
            received++;
            Console.Write(d.TextDelta);
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine($"[cancelled] received {received} deltas before cancellation");
}

Console.WriteLine();
Console.WriteLine("Done.");

await app.StopAsync();

// ---- Slow streaming provider: 30 deltas × 30ms ----
sealed class SlowStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
{
    public string ProviderName => "slow";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse("(buffered)", ModelId: "slow"));

    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 1; i <= 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
            yield return new CompletionUpdate($"{i} ");
        }
    }
}
