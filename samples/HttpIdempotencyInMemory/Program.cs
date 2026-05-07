// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// HttpIdempotencyInMemory — wire the v0.11 idempotency middleware; retry shows Idempotency-Replayed: true.
//
// Run: dotnet run --project samples/HttpIdempotencyInMemory
// Env: none (deterministic, no API key)
// Docs: docs/concepts/idempotency.md
//
// Sends two identical POST /v1/agents/{id}/invoke requests with the same
// Idempotency-Key header. The first call hits the agent; the second is served
// from the in-memory idempotency store without re-invoking the agent.
// The Idempotency-Replayed: true response header proves the cache was used.

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

// ---- server ----
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:0");

var registry  = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(new ScriptedProvider());
var lifecycle = new AgentLifecycleManager(registry, runtime);

await lifecycle.CreateAsync(
    new AgentManifest(
        Id:          "echo",
        Version:     "1.0",
        Handler:     new AgentHandlerRef("declarative"),
        Protocols:   [],
        Tools:       [],
        Description: "Scripted echo agent for idempotency demo."),
    CancellationToken.None);

builder.Services.AddSingleton<IAgentRegistry>(registry);
builder.Services.AddSingleton<IAgentRuntime>(runtime);
builder.Services.AddSingleton<IAgentLifecycleManager>(lifecycle);
builder.Services.AddAgentControlPlane();
builder.Services.AddAgentControlPlaneIdempotency();

var app = builder.Build();
// mount idempotency middleware before endpoint dispatch
app.UseAgentControlPlaneIdempotency();
app.MapAgentControlPlane("/v1");

await app.StartAsync();
var serverUrl = app.Urls.First();
Console.WriteLine($"Server: {serverUrl}");
Console.WriteLine();

// ---- client: two calls with the same Idempotency-Key ----
var http = new HttpClient { BaseAddress = new Uri(serverUrl) };
const string idempotencyKey = "demo-key-invoke-001";
var bodyJson = JsonSerializer.Serialize(new { text = "Hello from idempotency demo!" });

Console.WriteLine("== call 1 — first invocation ==");
var result1 = await InvokeWithKeyAsync(http, idempotencyKey, bodyJson);
Console.WriteLine($"  status:    {(int)result1.StatusCode}");
Console.WriteLine($"  replayed:  {result1.Replayed}");
Console.WriteLine($"  body:      {result1.Body[..Math.Min(80, result1.Body.Length)]}");

Console.WriteLine();
Console.WriteLine("== call 2 — same key + same body (expect replay) ==");
var result2 = await InvokeWithKeyAsync(http, idempotencyKey, bodyJson);
Console.WriteLine($"  status:    {(int)result2.StatusCode}");
Console.WriteLine($"  replayed:  {result2.Replayed}");
Console.WriteLine($"  body:      {result2.Body[..Math.Min(80, result2.Body.Length)]}");

Console.WriteLine();
Console.WriteLine($"bodies match: {result1.Body == result2.Body}");
Console.WriteLine($"agent invoke count: {ScriptedProvider.InvokeCount} (expected 1)");

await app.StopAsync();

// ---- helper ----
static async Task<(System.Net.HttpStatusCode StatusCode, string Replayed, string Body)> InvokeWithKeyAsync(
    HttpClient http, string key, string bodyJson)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/agents/echo/invoke")
    {
        Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
    };
    req.Headers.TryAddWithoutValidation("Idempotency-Key", key);
    using var resp = await http.SendAsync(req);
    var replayed = resp.Headers.TryGetValues("Idempotency-Replayed", out var vals)
        ? vals.First() : "(none)";
    var body = await resp.Content.ReadAsStringAsync();
    return (resp.StatusCode, replayed, body);
}

// ---- Scripted provider ----
sealed class ScriptedProvider : ICompletionProvider
{
    public static int InvokeCount { get; private set; }
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        InvokeCount++;
        return Task.FromResult(new CompletionResponse("Echo reply from the scripted agent.", ModelId: "scripted"));
    }
}
