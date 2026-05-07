// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// GraphPowerFxPredicates — inline PowerFx expressions for graph edge routing.
//
// Run: dotnet run --project samples/GraphPowerFxPredicates
// Prereq: dotnet pack src/Vais.Agents.Core.PowerFx -o artifacts/packages/
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/concepts/graph-orchestration.md
//
// Graph loaded from research-graph.yaml:
//   planner → analyst  (when: "=Not(IsBlank(Local.research_plan))")
//   planner → end      (when: "=IsBlank(Local.research_plan)")
//
// Run 1: planner returns a non-blank plan → graph routes to analyst.
// Run 2: planner returns empty plan → graph routes directly to end (analyst skipped).
//
// PowerFxGraphExpressionEvaluator is the only addition beyond standard graph wiring.
// No IGraphEdgePredicate class required — edges declare conditions inline as "=..." strings.

using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Control.Manifests;
using Vais.Agents.Core;
using Vais.Agents.Core.PowerFx;
using Vais.Agents.Hosting.InMemory;

// ── load manifest from YAML ──
var loader    = new YamlAgentGraphManifestLoader();
var manifests = await loader.LoadFromFileAsync(
    Path.Combine(AppContext.BaseDirectory, "research-graph.yaml"));
var manifest = manifests[0];

// ── build the agent registry ──
var registry  = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(new ScriptedProvider());
var lifecycle = new AgentLifecycleManager(registry, runtime);

foreach (var id in new[] { "planner-agent", "analyst-agent" })
    await lifecycle.CreateAsync(
        new AgentManifest(id, "1.0", new AgentHandlerRef("declarative"), [], []),
        CancellationToken.None);

// ── orchestrator with PowerFx expression evaluator ──
// PowerFxGraphExpressionEvaluator evaluates "=..." edge predicates against Local.* state keys.
var graph = new InProcessGraphOrchestrator(
    manifest, registry, lifecycle, new InMemoryCheckpointer(),
    expressionEvaluator: new PowerFxGraphExpressionEvaluator());
var ctx = new AgentContext(AgentName: "research-graph");

static string Fmt(AgentGraphEvent evt) => evt switch
{
    GraphStarted  e => $"  ► GraphStarted   entry={e.EntryNodeId}",
    NodeStarted   e => $"    NodeStarted    [{e.NodeKind}] {e.NodeId}",
    NodeCompleted e => $"    NodeCompleted  {e.NodeId}",
    EdgeTraversed e => $"    EdgeTraversed  {e.From} → {e.To}",
    StateUpdated  e => $"    StateUpdated   keys=[{string.Join(", ", e.ChangedKeys)}]",
    GraphCompleted _ => "  ✓ GraphCompleted",
    _             e => $"    {e.GetType().Name}",
};

// ── run 1: non-blank plan → routes to analyst ──
Console.WriteLine("== run 1 — non-blank plan → analyst ==");
var state1 = new Dictionary<string, JsonElement>
{
    ["query"] = JsonSerializer.SerializeToElement("AI trends in healthcare"),
};
await foreach (var evt in graph.StreamAsync(state1, ctx))
    Console.WriteLine(Fmt(evt));

Console.WriteLine();

// ── run 2: blank plan → routes to end (analyst skipped) ──
Console.WriteLine("== run 2 — blank plan → end (analyst skipped) ==");
var state2 = new Dictionary<string, JsonElement>
{
    ["query"] = JsonSerializer.SerializeToElement("skip"),
};
await foreach (var evt in graph.StreamAsync(state2, ctx))
    Console.WriteLine(Fmt(evt));

Console.WriteLine();
Console.WriteLine("Done.");

// ── scripted provider ──
// Dispatches on state content:
//   no "research_plan" key → planner; returns plan (or empty if query is "skip")
//   "research_plan" present → analyst; returns summary
sealed class ScriptedProvider : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var text = req.History.LastOrDefault(t => t.Role == AgentChatRole.User)?.Text ?? "";
        if (!text.Contains("\"research_plan\"", StringComparison.Ordinal))
        {
            var plan = text.Contains("\"skip\"", StringComparison.Ordinal)
                ? ""
                : "Analyze AI trends and their impact on healthcare workflows.";
            return Task.FromResult(new CompletionResponse(
                JsonSerializer.Serialize(new { research_plan = plan }), "scripted"));
        }
        return Task.FromResult(
            new CompletionResponse("Analysis: AI is reshaping healthcare diagnostics and workflows.", "scripted"));
    }
}
