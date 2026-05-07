// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// AgentGraphMaf — run the same triage graph using MafGraphOrchestrator (Microsoft Agent Framework).
//
// Run: dotnet run --project samples/AgentGraphMaf
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/concepts/graph-orchestration.md
//
// Identical graph shape to AgentGraphInProcess; output should be the same.
// The key difference: MafGraphOrchestrator projects the manifest onto MAF Workflows,
// unlocking fan-out / fan-in topologies (concurrent edges) that InProcess doesn't support.

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

foreach (var id in new[] { "classifier", "support", "sales" })
{
    await lifecycle.CreateAsync(
        new AgentManifest(id, "1.0", new AgentHandlerRef("declarative"), [], []),
        CancellationToken.None);
}

// ── define the graph manifest in C# ──
var supportValue = JsonSerializer.SerializeToElement("support");

var manifest = new AgentGraphManifest(
    Id:      "triage-graph",
    Version: "1.0",
    Entry:   "classify",
    Nodes:
    [
        new GraphNode("classify",      "Agent", Ref: new GraphAgentRef("classifier"),
            StateBindings: new GraphStateBindings(Output: ["category"])),
        new GraphNode("support-reply", "Agent", Ref: new GraphAgentRef("support")),
        new GraphNode("sales-reply",   "Agent", Ref: new GraphAgentRef("sales")),
        new GraphNode("end",           "End"),
    ],
    Edges:
    [
        new GraphEdge("classify",      "support-reply",
            When: new GraphEdgePredicate.PropertyMatcher("category", GraphPredicateOperator.Eq, supportValue)),
        new GraphEdge("classify",      "sales-reply"),
        new GraphEdge("support-reply", "end"),
        new GraphEdge("sales-reply",   "end"),
    ],
    Description: "Triage graph on MAF: classify → support or sales branch.");

// ── create the MAF orchestrator (no checkpointer needed for basic use) ──
var graph = new MafGraphOrchestrator(manifest, registry, lifecycle);
var ctx   = new AgentContext(AgentName: "triage-graph");

// ── 1. stream events ──
Console.WriteLine("== streaming run ==");
var initialState = new Dictionary<string, JsonElement>
{
    ["query"] = JsonSerializer.SerializeToElement("I need help with my account."),
};
await foreach (var evt in graph.StreamAsync(initialState, ctx))
{
    Console.WriteLine(evt switch
    {
        GraphStarted   e => $"  ► GraphStarted   entry={e.EntryNodeId}",
        NodeStarted    e => $"    NodeStarted    [{e.NodeKind}] {e.NodeId}",
        NodeCompleted  e => $"    NodeCompleted  {e.NodeId}",
        EdgeTraversed  e => $"    EdgeTraversed  {e.From} → {e.To}",
        StateUpdated   e => $"    StateUpdated   keys=[{string.Join(", ", e.ChangedKeys)}]",
        GraphCompleted e => $"  ✓ GraphCompleted",
        _              => $"    {evt.GetType().Name}",
    });
}

Console.WriteLine();

// ── 2. unary invoke ──
Console.WriteLine("== unary invoke ==");
var buyState = new Dictionary<string, JsonElement>
{
    ["query"] = JsonSerializer.SerializeToElement("Buy the enterprise plan."),
};
var result = await graph.InvokeAsync(buyState, ctx);
Console.WriteLine($"  query    = {result["query"].GetString()}");
Console.WriteLine($"  category = {(result.TryGetValue("category", out var cat) ? cat.GetString() : "(none)")}");

// Content-based dispatch — same as AgentGraphInProcess and AgentGraphYamlLoader.
sealed class ScriptedProvider : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var text = req.History.LastOrDefault(t => t.Role == AgentChatRole.User)?.Text ?? "";
        if (text.Contains("\"category\"", StringComparison.Ordinal))
        {
            return text.Contains("\"sales\"", StringComparison.Ordinal)
                ? Task.FromResult(new CompletionResponse("Product demo scheduled by sales team.", "scripted"))
                : Task.FromResult(new CompletionResponse("Issue resolved via support team.",      "scripted"));
        }
        return Task.FromResult(new CompletionResponse(
            text.Contains("Buy", StringComparison.OrdinalIgnoreCase)
                ? """{"category":"sales"}"""
                : """{"category":"support"}""",
            "scripted"));
    }
}
