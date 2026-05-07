// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// LlmGatewayMiddleware — compose LLM gateway middleware in C#:
//   fallback, semantic cache, structured-output (JSON) validation.
//
// Run: dotnet run --project samples/LlmGatewayMiddleware
// Prereq: gateway packages built to artifacts/packages/ (see README)
// Env: none (scripted providers, no API key)
// Docs: docs/concepts/llm-gateway.md
//
// Three passes on separate agents:
//  1. LlmFallbackMiddleware  — primary throws, backup provider takes over
//  2. LlmSemanticCacheMiddleware — second identical call returns cached response
//  3. LlmJsonOutputMiddleware<T> — response validated as JSON-deserializable WeatherReport

using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Gateways.Fallback;
using Vais.Agents.Gateways.SemanticCache;
using Vais.Agents.Gateways.StructuredOutput;

// ---- 1: LlmFallbackMiddleware ----
Console.WriteLine("== 1 — LlmFallbackMiddleware (primary throws → backup takes over) ==");
var flaky  = new FlakyProvider();
var backup = new BackupProvider();
// Pool: try providers in order until one succeeds.
var pool = new InMemoryFallbackProviderPool(flaky, backup);
var agent1 = new StatefulAiAgent(
    flaky,   // primary — irrelevant when middleware is active; pool drives provider selection
    new StatefulAgentOptions
    {
        GatewayMiddleware = [new LlmFallbackMiddleware(pool)],
    });
var r1 = await agent1.AskAsync("ping");
Console.WriteLine($"  response:       \"{r1}\"");
Console.WriteLine($"  flaky attempts: {flaky.Attempts}  (threw on attempt 1)");
Console.WriteLine($"  backup used:    {backup.Used}");
Console.WriteLine();

// ---- 2: LlmSemanticCacheMiddleware ----
Console.WriteLine("== 2 — LlmSemanticCacheMiddleware (same text → cache hit on 2nd call) ==");
var cacheStore = new InMemorySemanticCacheStore();
var counter    = new CountingProvider();
var agent2 = new StatefulAiAgent(
    counter,
    new StatefulAgentOptions
    {
        GatewayMiddleware = [new LlmSemanticCacheMiddleware(cacheStore)],
    });
const string question = "What is the capital of France?";
var r2a = await agent2.AskAsync(question);
var r2b = await agent2.AskAsync(question);   // same text → should hit cache
Console.WriteLine($"  call 1: \"{r2a}\"");
Console.WriteLine($"  call 2: \"{r2b}\"  (served from cache)");
Console.WriteLine($"  provider called: {counter.Count} time(s)  (expected 1)");
Console.WriteLine($"  bodies match:    {r2a == r2b}");
Console.WriteLine();

// ---- 3: LlmJsonOutputMiddleware<WeatherReport> ----
Console.WriteLine("== 3 — LlmJsonOutputMiddleware<WeatherReport> (JSON output validated) ==");
var jsonAgent = new StatefulAiAgent(
    new JsonProvider(),
    new StatefulAgentOptions
    {
        GatewayMiddleware = [new LlmJsonOutputMiddleware<WeatherReport>()],
    });
var r3 = await jsonAgent.AskAsync("weather in Tokyo");
var report = JsonSerializer.Deserialize<WeatherReport>(r3)!;
Console.WriteLine($"  raw text:  {r3}");
Console.WriteLine($"  city={report.City}  tempC={report.TempC}  condition={report.Condition}");
Console.WriteLine();
Console.WriteLine("Done.");

// ---- scripted providers ----
sealed class FlakyProvider : ICompletionProvider
{
    public int Attempts;
    public string ProviderName => "flaky";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        Attempts++;
        throw new InvalidOperationException($"transient failure (attempt {Attempts})");
    }
}

sealed class BackupProvider : ICompletionProvider
{
    public bool Used;
    public string ProviderName => "backup";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        Used = true;
        return Task.FromResult(new CompletionResponse("Backup reply.", ModelId: "backup"));
    }
}

sealed class CountingProvider : ICompletionProvider
{
    public int Count;
    public string ProviderName => "counting";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        Count++;
        return Task.FromResult(new CompletionResponse("Paris.", ModelId: "counting"));
    }
}

sealed class JsonProvider : ICompletionProvider
{
    public string ProviderName => "json";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(
            """{"city":"Tokyo","tempC":18,"condition":"Sunny"}""",
            ModelId: "json"));
}

sealed record WeatherReport(string City, int TempC, string Condition);
