// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// AgentGraphResumeOnOrleans — interrupt a graph mid-run, checkpoint state to Orleans,
// then resume from the persisted checkpoint in the same process.
//
// Run: dotnet run --project samples/AgentGraphResumeOnOrleans
// Env: none (in-memory Orleans cluster, no external services)
// Docs: docs/concepts/graph-orchestration.md
//
// Graph shape:
//   classify → approve (Interrupt) → reply → end
//
// Flow:
//   Run 1: classify fires → approve node pauses → OrleansCheckpointer saves state
//   Run 2: load checkpoint → resume from 'approve' → reply fires → end

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Vais.Agents;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Hosting.Orleans;

// ── start an in-process Orleans silo ──
var host = Host.CreateDefaultBuilder()
    .UseOrleans(silo =>
    {
        silo.UseLocalhostClustering();
        silo.AddMemoryGrainStorage(AiAgentGrain.StorageName);
    })
    .Build();

await host.StartAsync();

var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var checkpointer = new OrleansCheckpointer(grainFactory);

// ── build the agent registry ──
var registry  = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(new ScriptedProvider());
var lifecycle = new AgentLifecycleManager(registry, runtime);

foreach (var id in new[] { "classifier", "reply-agent" })
{
    await lifecycle.CreateAsync(
        new AgentManifest(id, "1.0", new AgentHandlerRef("declarative"), [], []),
        CancellationToken.None);
}

// ── define the graph manifest ──
var manifest = new AgentGraphManifest(
    Id:      "approval-graph",
    Version: "1.0",
    Entry:   "classify",
    Nodes:
    [
        new GraphNode("classify", "Agent", Ref: new GraphAgentRef("classifier"),
            StateBindings: new GraphStateBindings(Output: ["category"])),
        new GraphNode("approve",  "Interrupt", InterruptReason: "Pending human review"),
        new GraphNode("reply",    "Agent", Ref: new GraphAgentRef("reply-agent")),
        new GraphNode("end",      "End"),
    ],
    Edges:
    [
        new GraphEdge("classify", "approve"),
        new GraphEdge("approve",  "reply"),
        new GraphEdge("reply",    "end"),
    ],
    Description: "Classify → interrupt for approval → reply.");

// ── create the orchestrator with Orleans checkpointer ──
var graph = new InProcessGraphOrchestrator(manifest, registry, lifecycle, checkpointer);
var ctx   = new AgentContext(AgentName: "approval-graph");

// ── Phase 1: run until the interrupt node ──
Console.WriteLine("== phase 1: run until interrupt ==");
var initialState = new Dictionary<string, JsonElement>
{
    ["query"] = JsonSerializer.SerializeToElement("I need help with my account."),
};

string? runId = null;
string? interruptedAtNode = null;
await foreach (var evt in graph.StreamAsync(initialState, ctx))
{
    Console.WriteLine(evt switch
    {
        GraphStarted   e => $"  ► GraphStarted   entry={e.EntryNodeId}",
        NodeStarted    e => $"    NodeStarted    [{e.NodeKind}] {e.NodeId}",
        NodeCompleted  e => $"    NodeCompleted  {e.NodeId}",
        EdgeTraversed  e => $"    EdgeTraversed  {e.From} → {e.To}",
        StateUpdated   e => $"    StateUpdated   keys=[{string.Join(", ", e.ChangedKeys)}]",
        GraphInterrupted e => $"  ⏸ GraphInterrupted  node={e.NodeId}  reason={e.Reason}  runId={e.RunId}",
        _              => $"    {evt.GetType().Name}",
    });
    if (evt is GraphInterrupted gi)
    {
        runId            = gi.RunId;
        interruptedAtNode = gi.NodeId;
    }
}

Console.WriteLine();

// ── Phase 2: load the Orleans checkpoint ──
Console.WriteLine("== phase 2: load checkpoint from Orleans ==");
var checkpoint = await checkpointer.LoadAsync(runId!);
Console.WriteLine($"  runId       = {checkpoint!.RunId}");
Console.WriteLine($"  nextNode    = {checkpoint.NextNodeId}");
Console.WriteLine($"  category    = {(checkpoint.State.TryGetValue("category", out var cat) ? cat.GetString() : "(none)")}");
Console.WriteLine($"  isComplete  = {checkpoint.IsComplete}");

Console.WriteLine();

// ── Phase 3: resume from the checkpoint ──
Console.WriteLine("== phase 3: resume from checkpoint ==");
await foreach (var evt in graph.ResumeStreamAsync(checkpoint, resumePayload: null, ctx))
{
    Console.WriteLine(evt switch
    {
        GraphResumed   e => $"  ► GraphResumed   from={e.ResumedFromNodeId}",
        NodeStarted    e => $"    NodeStarted    [{e.NodeKind}] {e.NodeId}",
        NodeCompleted  e => $"    NodeCompleted  {e.NodeId}",
        EdgeTraversed  e => $"    EdgeTraversed  {e.From} → {e.To}",
        StateUpdated   e => $"    StateUpdated   keys=[{string.Join(", ", e.ChangedKeys)}]",
        GraphCompleted e => $"  ✓ GraphCompleted",
        _              => $"    {evt.GetType().Name}",
    });
}

await host.StopAsync();

// Content-based dispatch — same as other graph samples.
sealed class ScriptedProvider : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var text = req.History.LastOrDefault(t => t.Role == AgentChatRole.User)?.Text ?? "";
        if (text.Contains("\"category\"", StringComparison.Ordinal))
        {
            // reply-agent: last message is the classifier's JSON; dispatch on category.
            return text.Contains("\"sales\"", StringComparison.Ordinal)
                ? Task.FromResult(new CompletionResponse("Product demo scheduled by sales team.", "scripted"))
                : Task.FromResult(new CompletionResponse("Issue resolved via support team.",      "scripted"));
        }
        // Classifier: return JSON for output-binding extraction.
        return Task.FromResult(new CompletionResponse(
            text.Contains("Buy", StringComparison.OrdinalIgnoreCase)
                ? """{"category":"sales"}"""
                : """{"category":"support"}""",
            "scripted"));
    }
}
