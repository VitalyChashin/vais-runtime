// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents;
using Vais.Agents.Core;

// ---------------------------------------------------------------------------
// GraphCodeAuthored — compose and run a multi-agent graph entirely in C#.
// No YAML, no runtime container required. Uses InMemoryAgentRegistry +
// InMemoryAgentGraphRegistry + InProcessGraphOrchestrator + EchoingProvider
// so the sample is hermetic (no API key, no Docker).
// ---------------------------------------------------------------------------

// 1. Shared registries ---------------------------------------------------------
var agentRegistry = new InMemoryAgentRegistry();
var graphRegistry = new InMemoryAgentGraphRegistry();
var lifecycle     = new InMemoryAgentLifecycleManager(agentRegistry);
var checkpointer  = new InMemoryCheckpointer();

// 2. Register two agents -------------------------------------------------------
agentRegistry.Register(new AgentManifest(
    Id:      "step-a",
    Version: "1.0",
    Handler: new AgentHandlerRef(TypeName: "declarative"),
    Description: "First step — prepends [A] to the input."));

agentRegistry.Register(new AgentManifest(
    Id:      "step-b",
    Version: "1.0",
    Handler: new AgentHandlerRef(TypeName: "declarative"),
    Description: "Second step — appends [B] to whatever it receives."));

// 3. Build the graph manifest --------------------------------------------------
var manifest = new AgentGraphManifest(
    Id:    "two-step-pipeline",
    Version: "1.0",
    Entry: "run-a",
    Nodes:
    [
        new GraphNode(Id: "run-a", Kind: "Agent",
            Ref: new GraphAgentRef(Id: "step-a", Version: "1.0"),
            StateBindings: new GraphStateBindings(Input: ["input"], Output: ["a_output"])),
        new GraphNode(Id: "run-b", Kind: "Agent",
            Ref: new GraphAgentRef(Id: "step-b", Version: "1.0"),
            StateBindings: new GraphStateBindings(Input: ["a_output"])),
        new GraphNode(Id: "done", Kind: "End"),
    ],
    Edges:
    [
        new GraphEdge(From: "run-a", To: "run-b"),
        new GraphEdge(From: "run-b", To: "done"),
    ],
    Description: "A two-step linear pipeline authored entirely in C#.");

graphRegistry.Register(manifest);

// 4. Wire a DI container -------------------------------------------------------
var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<ICompletionProvider>(new EchoingProvider());
services.AddSingleton(agentRegistry);
services.AddSingleton<IAgentRegistry>(agentRegistry);
services.AddSingleton<IAgentGraphRegistry>(graphRegistry);

var sp = services.BuildServiceProvider();
var provider = sp.GetRequiredService<ICompletionProvider>();

// 5. Create the graph and invoke -----------------------------------------------
var graph = new InProcessGraphOrchestrator<PipelineState>(
    manifest:   manifest,
    registry:   agentRegistry,
    lifecycle:  lifecycle,
    checkpointer: checkpointer);

var ctx = new AgentContext(sessionId: "demo", agentId: "two-step-pipeline");

// Unary invoke — returns final state.
var result = await graph.InvokeAsync(new PipelineState(Input: "Hello from GraphCodeAuthored!"), ctx);
Console.WriteLine($"Final state:");
Console.WriteLine($"  input    = {result.Input}");
Console.WriteLine($"  a_output = {result.AOutput ?? "(empty)"}");
Console.WriteLine();

// Streaming invoke — observe every event in the BSP loop.
Console.WriteLine("Streaming events:");
await foreach (var evt in graph.StreamAsync(new PipelineState(Input: "streaming run"), ctx))
{
    Console.WriteLine($"  [{evt.GetType().Name,28}] graphId={evt.GraphId} runId={evt.RunId[..8]}…");
}

Console.WriteLine();
Console.WriteLine("Done.");

// ---- Typed graph state -------------------------------------------------------
record PipelineState(string Input, string? AOutput = null);

// ---- Deterministic echo provider for hermetic demos -------------------------
sealed class EchoingProvider : ICompletionProvider
{
    public string ProviderName => "echo";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var userText = req.History.Count > 0 ? req.History[^1].Text : "";
        return Task.FromResult(new CompletionResponse($"[echo] {userText}", ModelId: "echo"));
    }
}
