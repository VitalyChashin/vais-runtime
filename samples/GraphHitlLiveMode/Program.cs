// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// GraphHitlLiveMode — live-mode HITL: MafGraphOrchestrator stays open while an inline
// handler decides to approve or abort.
//
// Run: dotnet run --project samples/GraphHitlLiveMode
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/concepts/graph-orchestration.md
//
// Graph: draft → review (Interrupt) → publish → end.
// Run 1: handler approves (non-null state) → graph continues to publish.
// Run 2: handler aborts (null) → GraphHitlAbortedException caught.
//
// Contrast with ToolGuardrailsAndInterrupt (halt mode):
//   halt mode — graph pauses; caller must resume from a saved checkpoint in a later call.
//   live mode — graph stays open; handler is an inline async callback in the same iteration.

using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Orchestration.Graph.MicrosoftAgentFramework;

// ── build the agent registry ──
var registry  = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(new ScriptedProvider());
var lifecycle = new AgentLifecycleManager(registry, runtime);

foreach (var id in new[] { "drafter", "publisher" })
    await lifecycle.CreateAsync(
        new AgentManifest(id, "1.0", new AgentHandlerRef("declarative"), [], []),
        CancellationToken.None);

// ── graph: draft → review (Interrupt) → publish → end ──
var manifest = new AgentGraphManifest(
    Id:      "content-pipeline",
    Version: "1.0",
    Entry:   "draft",
    Nodes:
    [
        new GraphNode("draft",   "Agent",     Ref: new GraphAgentRef("drafter"),
            StateBindings: new GraphStateBindings(Output: ["draft"])),
        new GraphNode("review",  "Interrupt", InterruptReason: "Pending editorial review"),
        new GraphNode("publish", "Agent",     Ref: new GraphAgentRef("publisher"),
            StateBindings: new GraphStateBindings(Output: ["published"])),
        new GraphNode("end",     "End"),
    ],
    Edges:
    [
        new GraphEdge("draft",   "review"),
        new GraphEdge("review",  "publish"),
        new GraphEdge("publish", "end"),
    ],
    Description: "Content pipeline: draft → editorial review → publish.");

var graph = new MafGraphOrchestrator(manifest, registry, lifecycle);
var ctx   = new AgentContext(AgentName: "content-pipeline");

static string Fmt(AgentGraphEvent evt) => evt switch
{
    GraphStarted     e => $"  ► GraphStarted    entry={e.EntryNodeId}",
    NodeStarted      e => $"    NodeStarted     [{e.NodeKind}] {e.NodeId}",
    NodeCompleted    e => $"    NodeCompleted   {e.NodeId}",
    EdgeTraversed    e => $"    EdgeTraversed   {e.From} → {e.To}",
    StateUpdated     e => $"    StateUpdated    keys=[{string.Join(", ", e.ChangedKeys)}]",
    GraphInterrupted e => $"    GraphInterrupted nodeId={e.NodeId}  reason=\"{e.Reason}\"",
    GraphFailed      e => $"  ✗ GraphFailed     {e.ErrorType}",
    GraphCompleted   _ => "  ✓ GraphCompleted",
    _                e => $"    {e.GetType().Name}",
};

// ── run 1: approve ──
Console.WriteLine("== run 1 — approved ==");
var state1 = new Dictionary<string, JsonElement>
{
    ["topic"] = JsonSerializer.SerializeToElement("AI agents"),
};
await foreach (var evt in graph.StreamWithHitlAsync(
    state1, ctx,
    handleInterrupt: (interrupted, ct) =>
    {
        Console.WriteLine($"  [handler] reason=\"{interrupted.Reason}\" → approving");
        return ValueTask.FromResult<IDictionary<string, JsonElement>?>(
            new Dictionary<string, JsonElement>
            {
                ["approved"] = JsonSerializer.SerializeToElement(true),
            });
    }))
{
    Console.WriteLine(Fmt(evt));
}

Console.WriteLine();

// ── run 2: abort ──
Console.WriteLine("== run 2 — aborted ==");
var state2 = new Dictionary<string, JsonElement>
{
    ["topic"] = JsonSerializer.SerializeToElement("AI agents"),
};
try
{
    await foreach (var evt in graph.StreamWithHitlAsync(
        state2, ctx,
        handleInterrupt: (interrupted, ct) =>
        {
            Console.WriteLine($"  [handler] reason=\"{interrupted.Reason}\" → aborting (null)");
            return ValueTask.FromResult<IDictionary<string, JsonElement>?>(null);
        }))
    {
        Console.WriteLine(Fmt(evt));
    }
}
catch (GraphHitlAbortedException ex)
{
    Console.WriteLine($"  caught: GraphHitlAbortedException nodeId={ex.NodeId}");
}

Console.WriteLine();
Console.WriteLine("Done.");

// ── scripted provider ──
// Dispatches on state content: no "draft" key → drafter agent; "draft" present → publisher.
sealed class ScriptedProvider : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var text = req.History.LastOrDefault(t => t.Role == AgentChatRole.User)?.Text ?? "";
        return Task.FromResult(
            text.Contains("\"draft\"", StringComparison.Ordinal)
                ? new CompletionResponse("""{"published":"Article published successfully."}""", "scripted")
                : new CompletionResponse("""{"draft":"An introduction to AI agents."}""",        "scripted"));
    }
}
