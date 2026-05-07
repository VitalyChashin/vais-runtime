// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// OpaPolicyGateLocal — gate agent creates on a model-provider allowlist; watch denials hit the audit log.
//
// Run: dotnet run --project samples/OpaPolicyGateLocal
// Prereq: opa run --server samples/OpaPolicyGateLocal/policy.rego
// Env: none (no API key)
// Docs: docs/concepts/policy.md
//
// Wires an AgentLifecycleManager to an OPA policy engine + a logger-backed audit log.
// Sends two CreateAsync calls: one with an allowed model provider (openai) and one
// with a denied provider (blocked-llm). The first succeeds; the second throws
// AgentPolicyDeniedException. Both calls emit an audit-log entry to the console.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Control.Policy.Opa;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

const string opaBaseUrl = "http://localhost:8181";

// ---- probe OPA before building DI ----
using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
{
    try
    {
        var resp = await probe.GetAsync($"{opaBaseUrl}/health");
        if (!resp.IsSuccessStatusCode)
            return Bail($"OPA at {opaBaseUrl} returned {(int)resp.StatusCode} on /health.");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return Bail($"Cannot reach OPA at {opaBaseUrl}: {ex.Message}");
    }
}

Console.WriteLine($"OPA:   {opaBaseUrl}  ✓");
Console.WriteLine();

// ---- DI ----
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// Show Warning+ globally; let the audit category through at Information.
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Vais.Agents.Control.InProcess.LoggerAuditLog", LogLevel.Information);

builder.Services.AddOpaPolicyEngine(opts =>
{
    opts.BaseUrl  = new Uri(opaBaseUrl);
    opts.FailMode = OpaFailMode.Closed;   // deny all when OPA is unreachable
    opts.DecisionCacheTtl = TimeSpan.Zero; // disable cache so every call hits OPA
});
builder.Services.AddSingleton<IAuditLog, LoggerAuditLog>();

var app = builder.Build();

// ---- resolve + wire lifecycle manager ----
var policy    = app.Services.GetRequiredService<IAgentPolicyEngine>();
var audit     = app.Services.GetRequiredService<IAuditLog>();
var registry  = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(new ScriptedProvider());
var lifecycle = new AgentLifecycleManager(registry, runtime, policy: policy, audit: audit);

// ---- case 1: openai provider (allowed) ----
Console.WriteLine("== create 1 — openai provider (allowed) ==");
var allowedManifest = new AgentManifest(
    Id:          "agent-openai",
    Version:     "1.0",
    Handler:     new AgentHandlerRef("declarative"),
    Protocols:   [],
    Tools:       [])
{ Model = new ModelSpec("openai", "gpt-4o") };

try
{
    await lifecycle.CreateAsync(allowedManifest, CancellationToken.None);
    Console.WriteLine("  result: allowed ✓");
}
catch (AgentPolicyDeniedException ex)
{
    Console.WriteLine($"  result: denied  [{ex.Operation}] \"{ex.Reason}\"  (unexpected)");
}

Console.WriteLine();

// ---- case 2: blocked-llm provider (denied) ----
Console.WriteLine("== create 2 — blocked-llm provider (denied) ==");
var deniedManifest = new AgentManifest(
    Id:          "agent-blocked",
    Version:     "1.0",
    Handler:     new AgentHandlerRef("declarative"),
    Protocols:   [],
    Tools:       [])
{ Model = new ModelSpec("blocked-llm", "proprietary-v1") };

try
{
    await lifecycle.CreateAsync(deniedManifest, CancellationToken.None);
    Console.WriteLine("  result: allowed  (unexpected)");
}
catch (AgentPolicyDeniedException ex)
{
    Console.WriteLine($"  result: denied ✓  operation={ex.Operation}  reason=\"{ex.Reason}\"");
}

Console.WriteLine();
Console.WriteLine("Done.");
return 0;

// ---- helpers ----
static int Bail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("Start OPA with the sample policy before running:");
    Console.Error.WriteLine("  opa run --server samples/OpaPolicyGateLocal/policy.rego");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Download OPA: https://www.openpolicyagent.org/docs/latest/#running-opa");
    return 1;
}

// ---- scripted provider (not called; only lifecycle gating is demonstrated) ----
sealed class ScriptedProvider : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse("ok", ModelId: "scripted"));
}
