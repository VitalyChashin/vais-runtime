// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// McpServerStdio — host a StatefulAiAgent as an MCP tool over stdio.
//
// Demo (deterministic, no API key, exits after printing):
//   dotnet run --project McpServerStdio -- --demo
//
// Server mode (connect from Claude Desktop or any MCP stdio client):
//   dotnet run --project McpServerStdio
//   Add to Claude Desktop config:
//     { "mcpServers": { "greeter": {
//         "command": "dotnet",
//         "args": ["run", "--project", "<absolute-path>/McpServerStdio"]
//     }}}
//
// Docs: docs/guides/host-agents-as-mcp-tools.md

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Protocols.Mcp.Server;

// --- shared setup: one greeter agent with a scripted response ---
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

// --- demo mode: show registered tools + scripted round-trip, then exit ---
if (args.Contains("--demo"))
{
    Console.WriteLine("=== McpServerStdio — demo ===");
    Console.WriteLine();

    Console.WriteLine("tools/list (one MCP tool per registered agent):");
    await foreach (var m in registry.ListAsync(labelPrefix: null, CancellationToken.None))
    {
        Console.WriteLine($"  tool:  {m.Id} v{m.Version} — {m.Description}");
        Console.WriteLine("  input: { \"text\": string (required), \"sessionId\"?: string, \"resume\"?: { interruptId, ... } }");
    }
    Console.WriteLine();

    Console.WriteLine("tools/call  name=greeter  arguments={ text: \"Hi!\" }:");
    var result = await lifecycle.InvokeAsync(
        new AgentHandle("greeter", "1.0"),
        new AgentInvocationRequest("Hi!"),
        CancellationToken.None);
    Console.WriteLine($"  → \"{result.Text}\"");
    Console.WriteLine();

    Console.WriteLine("resources/list (manifest URI per agent):");
    Console.WriteLine("  agent://greeter/1.0/manifest");
    Console.WriteLine();
    Console.WriteLine("Run without --demo to start the real MCP stdio server.");
    return;
}

// --- server mode: stdin = MCP JSON-RPC in, stdout = MCP JSON-RPC out ---
// Console logging is suppressed so it doesn't corrupt the MCP stream on stdout.
var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(l => l.ClearProviders())
    .ConfigureServices(services =>
    {
        services.AddSingleton<IAgentRegistry>(registry);
        services.AddSingleton<IAgentLifecycleManager>(lifecycle);
        services.AddMcpAgentServerStdio(o =>
        {
            o.Name         = "sample-server";
            o.Version      = "0.15";
            o.Instructions = "Call the 'greeter' tool with { text: '...' }.";
        });
    })
    .Build();

await host.RunAsync();

// Scripted provider — returns a fixed response without calling any LLM.
sealed class ScriptedProvider(string reply) : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(reply, ModelId: "scripted-model"));
}
