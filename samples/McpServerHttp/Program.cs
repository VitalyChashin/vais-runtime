// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// McpServerHttp — host a StatefulAiAgent as an MCP tool over streamable-HTTP.
//
// Run: dotnet run --project samples/McpServerHttp
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/guides/host-agents-as-mcp-tools.md
//
// Starts an ASP.NET Core server on a random local port, connects a co-located
// McpClient via HttpClientTransport, then runs:
//   1. tools/list   — discover the greeter agent as an MCP tool
//   2. tools/call   — invoke the greeter agent
//   3. resources/list — list the agent manifest URIs

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Protocols.Mcp.Server;

// --- build the server ---
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:0");  // bind to an OS-assigned port

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
builder.Services.AddMcpAgentServerHttp(o =>
{
    o.Name    = "sample-http-server";
    o.Version = "0.15";
});

var app = builder.Build();
app.MapMcpAgentServer("/mcp");

await app.StartAsync();
var serverUrl = app.Urls.First();   // e.g. http://127.0.0.1:54321
Console.WriteLine($"Server: {serverUrl}/mcp");
Console.WriteLine();

// --- run the client ---
await using var transport = new HttpClientTransport(
    new HttpClientTransportOptions { Endpoint = new Uri($"{serverUrl}/mcp") });
await using var client = await McpClient.CreateAsync(transport);

// 1. tools/list
var tools = await client.ListToolsAsync();
Console.WriteLine($"tools/list → {tools.Count} tool(s):");
foreach (var t in tools)
{
    Console.WriteLine($"  • {t.Name} — {t.Description?.Split('\n')[0]}");
}
Console.WriteLine();

// 2. tools/call
var callResult = await client.CallToolAsync(
    "greeter",
    new Dictionary<string, object?> { ["text"] = "Hello!" });
var replyText = callResult.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
    .FirstOrDefault()?.Text ?? "(no text)";
Console.WriteLine($"tools/call greeter {{ text: \"Hello!\" }}:");
Console.WriteLine($"  → \"{replyText}\"");
Console.WriteLine();

// 3. resources/list
var resources = await client.ListResourcesAsync();
Console.WriteLine($"resources/list → {resources.Count} resource(s):");
foreach (var r in resources)
{
    Console.WriteLine($"  • {r.Uri} — {r.Description}");
}

await app.StopAsync();

// Scripted provider — returns a fixed reply without calling any LLM.
sealed class ScriptedProvider(string reply) : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(reply, ModelId: "scripted-model"));
}
