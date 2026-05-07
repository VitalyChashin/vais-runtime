// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// OpenApiSpecExplorer — inspect the shipped OpenAPI spec + its x-vais-type-urns extension.
//
// Run: dotnet run --project samples/OpenApiSpecExplorer
// Env: none (deterministic, no API key)
// Docs: docs/guides/openapi-spec.md
//
// Boots an in-process WebApplication with AddAgentControlPlaneOpenApi() +
// MapAgentControlPlaneOpenApi(), then fetches GET /openapi/v1.json, parses
// the document with System.Text.Json, and prints:
//   1. All paths defined in the spec.
//   2. For each path+method, any error responses annotated with x-vais-type-urns
//      — the stable URN set the VaisProblemDetailsOperationTransformer injects.

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

builder.Services.AddSingleton<IAgentRegistry>(registry);
builder.Services.AddSingleton<IAgentRuntime>(runtime);
builder.Services.AddSingleton<IAgentLifecycleManager>(lifecycle);
builder.Services.AddAgentControlPlane();
builder.Services.AddAgentControlPlaneOpenApi();   // register OpenAPI doc generator

var app = builder.Build();
app.MapAgentControlPlane("/v1");
app.MapAgentControlPlaneOpenApi();                // mount GET /openapi/{documentName}.json

await app.StartAsync();
var serverUrl = app.Urls.First();
Console.WriteLine($"Server: {serverUrl}");
Console.WriteLine();

// ---- client: fetch + parse the spec ----
var http   = new HttpClient { BaseAddress = new Uri(serverUrl) };
var spec   = await http.GetStringAsync("/openapi/v1.json");
var doc    = JsonDocument.Parse(spec);
var root   = doc.RootElement;

var openApiVersion = root.TryGetProperty("openapi", out var v) ? v.GetString() : "?";
Console.WriteLine($"OpenAPI {openApiVersion}  —  {root.GetProperty("info").GetProperty("title").GetString()}");
Console.WriteLine();

// ---- 1. All paths ----
Console.WriteLine("== paths ==");
if (root.TryGetProperty("paths", out var paths))
{
    foreach (var path in paths.EnumerateObject())
    {
        Console.WriteLine($"  {path.Name}");
    }
}

// ---- 2. x-vais-type-urns per error response ----
Console.WriteLine();
Console.WriteLine("== x-vais-type-urns annotations ==");
if (root.TryGetProperty("paths", out var paths2))
{
    foreach (var path in paths2.EnumerateObject())
    {
        foreach (var method in path.Value.EnumerateObject())
        {
            if (!method.Value.TryGetProperty("responses", out var responses)) continue;
            foreach (var response in responses.EnumerateObject())
            {
                if (!response.Value.TryGetProperty("x-vais-type-urns", out var urns)) continue;
                var urnList = string.Join(", ", urns.EnumerateArray().Select(u => u.GetString()));
                Console.WriteLine($"  {method.Name.ToUpperInvariant()} {path.Name}  [{response.Name}]  {urnList}");
            }
        }
    }
}

Console.WriteLine();
Console.WriteLine("Done.");

await app.StopAsync();

// ---- Scripted provider ----
sealed class ScriptedProvider : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse("OK", ModelId: "scripted"));
}
