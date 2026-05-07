// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// HttpStreamingInvoke — end-to-end SSE streaming over the HTTP control plane.
//
// Run: dotnet run --project samples/HttpStreamingInvoke
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/guides/stream-invocations-over-http.md
//
// Starts an ASP.NET Core server (random port) with AddAgentControlPlane() +
// MapAgentControlPlane(), then uses AgentControlPlaneClient.InvokeStreamEventsAsync
// to consume the SSE event stream and print every AgentEvent.

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
builder.WebHost.UseUrls("http://127.0.0.1:0");  // OS-assigned port

var registry  = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(new ScriptedStreamingProvider());
var lifecycle = new AgentLifecycleManager(registry, runtime);

await lifecycle.CreateAsync(
    new AgentManifest(
        Id:          "echo",
        Version:     "1.0",
        Handler:     new AgentHandlerRef("declarative"),
        Protocols:   [],
        Tools:       [],
        Description: "Scripted streaming echo agent."),
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

// ---- client ----
var http = new HttpClient { BaseAddress = new Uri(serverUrl) };
var client = new AgentControlPlaneClient(http);

var request = new AgentInvocationRequest(Text: "Tell me about streaming.");

Console.WriteLine("== InvokeStreamEventsAsync ==");
await foreach (var evt in client.InvokeStreamEventsAsync("echo", request, version: null, idempotencyKey: null, CancellationToken.None))
{
    var tag = evt switch
    {
        TurnStarted       => "TurnStarted  ",
        CompletionDelta d => $"Delta        \"{d.TextDelta}\"",
        TurnCompleted  c  => $"TurnCompleted  tokens={c.PromptTokens}+{c.CompletionTokens}",
        TurnFailed     f  => $"TurnFailed   {f.ErrorMessage}",
        _                 => evt.GetType().Name,
    };
    Console.WriteLine($"  {tag}");
}

Console.WriteLine();
Console.WriteLine("Done.");

await app.StopAsync();

// ---- Scripted streaming provider (5 word-chunk deltas) ----
sealed class ScriptedStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
{
    private static readonly string[] Words =
        "Streaming delivers agent responses token by token as they are generated.".Split(' ');

    public string ProviderName => "scripted";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(
            string.Join(" ", Words), ModelId: "scripted", PromptTokens: 10, CompletionTokens: 12));

#pragma warning disable CS1998
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var word in Words)
        {
            ct.ThrowIfCancellationRequested();
            yield return new CompletionUpdate(word + " ");
        }
        // terminal update carries token counts + model id
        yield return new CompletionUpdate(
            TextDelta: "",
            ModelId: "scripted",
            PromptTokens: 10,
            CompletionTokens: (int)Words.Length);
    }
#pragma warning restore CS1998
}
