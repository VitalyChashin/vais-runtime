// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// AgentGraphYamlLoader — load a graph manifest from YAML and run it in-process.
//
// Run: dotnet run --project samples/AgentGraphYamlLoader
// Env: none (deterministic, scripted provider, no API key)
// Docs: docs/concepts/graph-orchestration.md
//
// This sample is identical in behaviour to AgentGraphInProcess but the graph is
// defined in triage-graph.yaml rather than in C#.  Demonstrates:
//   - YamlAgentGraphManifestLoader for file-based or string-based YAML loading
//   - Same InProcessGraphOrchestrator runtime — no code changes needed

using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Control.Manifests;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

// ── load the graph manifest from YAML ──
var loader = new YamlAgentGraphManifestLoader();
var manifests = await loader.LoadFromFileAsync(
    Path.Combine(AppContext.BaseDirectory, "triage-graph.yaml"));
var manifest = manifests[0];

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

// ── create the orchestrator ──
var checkpointer = new InMemoryCheckpointer();
var graph = new InProcessGraphOrchestrator(manifest, registry, lifecycle, checkpointer);
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

// Content-based dispatch — same as AgentGraphInProcess.
// The classifier returns JSON so the output binding can extract 'category' into state.
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
