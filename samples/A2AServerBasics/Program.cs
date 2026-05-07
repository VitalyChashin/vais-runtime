// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// A2AServerBasics — host a StatefulAiAgent as an A2A endpoint.
//
// Run: dotnet run --project samples/A2AServerBasics
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/guides/host-agents-as-an-a2a-endpoint.md
//
// Starts an ASP.NET Core server on a randomly-chosen local port, then:
//   1. agent-card discovery — resolves the auto-derived AgentCard
//   2. message round-trip  — sends a plain user message, prints the reply
//
// The AgentCard is auto-derived from the AgentManifest by AgentCardBuilder.

using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Protocols.A2A.Server;

// Claim a free port before building the app so MapA2AAgentServer gets the real URL.
var port = FindFreePort();
var serverUrl = $"http://127.0.0.1:{port}";

// --- build the server ---
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(serverUrl);

var registry = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(
    new ScriptedProvider("Hello! I'm the greeter agent. How can I help?"));
var lifecycle = new AgentLifecycleManager(registry, runtime);

await lifecycle.CreateAsync(
    new AgentManifest(
        Id:          "greeter",
        Version:     "1.0",
        Handler:     new AgentHandlerRef("declarative"),
        Protocols:   [],
        Tools:       [],
        Description: "A friendly greeter agent."),
    CancellationToken.None);

builder.Services.AddSingleton<IAgentRegistry>(registry);
builder.Services.AddSingleton<IAgentLifecycleManager>(lifecycle);
builder.Services.AddA2AAgentServer();

var app = builder.Build();

// Mounts /agents/greeter  (JSON-RPC) and /agents/greeter/.well-known/agent-card.json.
app.MapA2AAgentServer(serverUrl);

await app.StartAsync();
Console.WriteLine($"Server: {serverUrl}/agents/greeter");
Console.WriteLine();

// --- run the client ---
var agentUrl = new Uri($"{serverUrl}/agents/greeter");
var client   = new A2AClient(agentUrl);

// 1. agent-card discovery — the card is auto-derived from the AgentManifest and served at
//    /agents/greeter/.well-known/agent-card.json; A2ACardResolver resolves it at that path.
var cardResolver = new A2ACardResolver(
    baseUrl:       new Uri(serverUrl),
    agentCardPath: "/agents/greeter/.well-known/agent-card.json");
var card = await cardResolver.GetAgentCardAsync();
Console.WriteLine($"agent-card: {card.Name} — {card.Description}");
Console.WriteLine();

// 2. message round-trip
var msg = new Message { Role = Role.User, MessageId = Guid.NewGuid().ToString("N") };
msg.Parts.Add(Part.FromText("Hello!"));
var response = await client.SendMessageAsync(new SendMessageRequest { Message = msg });

switch (response.PayloadCase)
{
    case SendMessageResponseCase.Message:
        Console.WriteLine($"reply: \"{response.Message!.Parts[0].Text}\"");
        break;
    case SendMessageResponseCase.Task:
        Console.WriteLine($"task: {response.Task!.Status.State} — {response.Task.Id}");
        break;
    default:
        Console.WriteLine("(no response)");
        break;
}

await app.StopAsync();

// Pick a free TCP port — used so MapA2AAgentServer gets the correct AgentCard URL.
static int FindFreePort()
{
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

// Scripted provider — returns a fixed reply without calling any LLM.
sealed class ScriptedProvider(string reply) : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(reply, ModelId: "scripted-model"));
}
