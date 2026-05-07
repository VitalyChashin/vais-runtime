// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// AgentGraphInProcess — build and run a branching multi-agent graph entirely in C#.
//
// Run: dotnet run --project samples/AgentGraphInProcess
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/concepts/graph-orchestration.md
//
// Graph shape:
//   classify → [category == "support"] → support-reply → end
//            → [always]                → sales-reply   → end
//
// Scripted provider dispatches by the text it receives:
//   <user query>   → classifier → returns {"category":"support"} or {"category":"sales"} JSON
//   {"category":*} → handler   → returns the domain reply
//
// The classifier returns JSON so the output binding can extract 'category' into state.
// Handlers receive the classifier's JSON as their input (the last messages entry).

using System.Text.Json;
using System.Text.Json.Serialization;
using Vais.Agents;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

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
//
// PropertyMatcher uses a JsonElement value for the right-hand comparison.
// JsonSerializer.SerializeToElement produces a string-kind element for "support".
var supportValue = JsonSerializer.SerializeToElement("support");

var manifest = new AgentGraphManifest(
    Id:      "triage-graph",
    Version: "1.0",
    Entry:   "classify",
    Nodes:
    [
        // Classifier: reads query from state, returns {"category":"..."} JSON.
        // Output binding extracts 'category' from the JSON response into state.
        new GraphNode("classify",      "Agent", Ref: new GraphAgentRef("classifier"),
            StateBindings: new GraphStateBindings(Output: ["category"])),
        // Handlers: no binding — receive last message (classifier JSON) via messages history.
        new GraphNode("support-reply", "Agent", Ref: new GraphAgentRef("support")),
        new GraphNode("sales-reply",   "Agent", Ref: new GraphAgentRef("sales")),
        new GraphNode("end",           "End"),
    ],
    Edges:
    [
        // Route to support when state["category"] == "support"; fall through to sales otherwise.
        new GraphEdge("classify",      "support-reply",
            When: new GraphEdgePredicate.PropertyMatcher("category", GraphPredicateOperator.Eq, supportValue)),
        new GraphEdge("classify",      "sales-reply"),   // always-true fallback
        new GraphEdge("support-reply", "end"),
        new GraphEdge("sales-reply",   "end"),
    ],
    Description: "Triage graph: classify → support or sales branch.");

// ── create the orchestrator ──
var checkpointer = new InMemoryCheckpointer();
var graph = new InProcessGraphOrchestrator<PipelineState>(manifest, registry, lifecycle, checkpointer);
var ctx   = new AgentContext(AgentName: "triage-graph");

// ── 1. stream events — observe every BSP step ──
Console.WriteLine("== streaming run ==");
await foreach (var evt in graph.StreamAsync(new PipelineState(Query: "I need help with my account."), ctx))
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

// ── 2. unary invoke — returns final typed state ──
Console.WriteLine("== unary invoke ==");
var result = await graph.InvokeAsync(new PipelineState(Query: "Buy the enterprise plan."), ctx);
Console.WriteLine($"  query    = {result.Query}");
Console.WriteLine($"  category = {result.Category ?? "(none)"}");

// ── types ──

// JsonPropertyName maps PascalCase C# properties to lowercase state-bag keys so
// InProcessGraphOrchestrator's ToBag / FromBag serialization round-trip correctly.
// "query" is the well-known fallback key that BuildAgentInputText forwards to agents.
record PipelineState(
    [property: JsonPropertyName("query")]    string  Query    = "",
    [property: JsonPropertyName("category")] string? Category = null);

// Content-based dispatch — stateless, safe for multiple graph runs:
//   user query      → classifier → {"category":"support"} or {"category":"sales"} JSON
//   {"category":*}  → handler   → domain reply
//
// The classifier returns structured JSON so the output binding can extract 'category'.
// Handlers receive the classifier's JSON string as input (via the messages accumulator).
sealed class ScriptedProvider : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var text = req.History.LastOrDefault(t => t.Role == AgentChatRole.User)?.Text ?? "";
        if (text.Contains("\"category\"", StringComparison.Ordinal))
        {
            // Handler invocation: last message is the classifier's JSON payload.
            return text.Contains("\"sales\"", StringComparison.Ordinal)
                ? Task.FromResult(new CompletionResponse("Product demo scheduled by sales team.", "scripted"))
                : Task.FromResult(new CompletionResponse("Issue resolved via support team.",      "scripted"));
        }
        // Classifier invocation: return JSON for output-binding extraction.
        return Task.FromResult(new CompletionResponse(
            text.Contains("Buy", StringComparison.OrdinalIgnoreCase)
                ? """{"category":"sales"}"""
                : """{"category":"support"}""",
            "scripted"));
    }
}
