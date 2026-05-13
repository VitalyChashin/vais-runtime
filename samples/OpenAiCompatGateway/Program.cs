// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// OpenAiCompatGateway — expose a Vais.Agents provider as a POST /v1/chat/completions endpoint
// compatible with any OpenAI SDK or curl command.
//
// Run: dotnet run --project samples/OpenAiCompatGateway
// Prereq: Vais.Agents.Gateways.OpenAiCompat packed to artifacts/packages/ (see README)
// Env: none (scripted provider, no API key)
// Docs: docs/guides/openai-compat-gateway.md
//
// Boots an in-process ASP.NET Core WebApplication with:
//   AddOpenAiCompatGateway()         — HTTP layer + context accessor
//   AddPassThroughIdentityResolver() — single-tenant dev: any bearer token accepted
//   AddInMemoryModelRouter(routes)   — "gpt-4o-mini" → ScriptedProvider
//   MapOpenAiCompat()                — POST /v1/chat/completions + GET /v1/models
//
// Three client calls demonstrate:
//   1. GET  /v1/models                              — list registered model aliases
//   2. POST /v1/chat/completions (stream: false)    — unary JSON response
//   3. POST /v1/chat/completions (stream: true)     — SSE deltas (text/event-stream)

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Gateways.OpenAiCompat;

// ---- server ----
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls("http://127.0.0.1:0");

// Both routing paths enabled by default.
// Override via config: Vais__OpenAiCompat__AgentRoutingEnabled=false
// or in code: AddOpenAiCompatGateway(o => o.GraphRoutingEnabled = false)
builder.Services.AddOpenAiCompatGateway();
builder.Services.AddPassThroughIdentityResolver();  // dev-only: accepts any bearer token
builder.Services.AddInMemoryModelRouter(routes =>
{
    var provider = new ScriptedProvider();
    routes.Add("gpt-4o-mini", new ModelRoute(provider, new ModelSpec("openai", "gpt-4o-mini")));
});

var app = builder.Build();
app.MapOpenAiCompat();   // POST /v1/chat/completions  +  GET /v1/models

await app.StartAsync();
var serverUrl = app.Urls.First();
Console.WriteLine($"Server: {serverUrl}");
Console.WriteLine();

var http = new HttpClient
{
    BaseAddress = new Uri(serverUrl),
    DefaultRequestHeaders = { { "Authorization", "Bearer dev-token" } },
};

// ---- 1: GET /v1/models ----
Console.WriteLine("== GET /v1/models ==");
var modelsJson = await http.GetStringAsync("/v1/models");
using (var doc = JsonDocument.Parse(modelsJson))
{
    foreach (var model in doc.RootElement.GetProperty("data").EnumerateArray())
        Console.WriteLine($"  {model.GetProperty("id").GetString()}");
}
Console.WriteLine();

// ---- 2: POST /v1/chat/completions (non-streaming) ----
Console.WriteLine("== POST /v1/chat/completions (stream: false) ==");
var body1 = JsonSerializer.Serialize(new
{
    model    = "gpt-4o-mini",
    messages = new[] { new { role = "user", content = "What is 2 + 2?" } },
    stream   = false,
});
using var resp1 = await http.PostAsync(
    "/v1/chat/completions",
    new StringContent(body1, Encoding.UTF8, "application/json"));
Console.WriteLine($"  status:  {(int)resp1.StatusCode}");
using var d1 = JsonDocument.Parse(await resp1.Content.ReadAsStringAsync());
Console.WriteLine($"  object:  {d1.RootElement.GetProperty("object").GetString()}");
Console.WriteLine($"  content: \"{d1.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()}\"");
Console.WriteLine($"  finish:  {d1.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString()}");
if (d1.RootElement.TryGetProperty("usage", out var usage))
    Console.WriteLine($"  usage:   prompt={usage.GetProperty("prompt_tokens").GetInt32()}  completion={usage.GetProperty("completion_tokens").GetInt32()}");
Console.WriteLine();

// ---- 3: POST /v1/chat/completions (streaming) ----
Console.WriteLine("== POST /v1/chat/completions (stream: true) ==");
var body2 = JsonSerializer.Serialize(new
{
    model    = "gpt-4o-mini",
    messages = new[] { new { role = "user", content = "Count to 3." } },
    stream   = true,
});
using var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
{
    Content = new StringContent(body2, Encoding.UTF8, "application/json"),
};
using var resp2 = await http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead);
Console.WriteLine($"  status:       {(int)resp2.StatusCode}");
Console.WriteLine($"  content-type: {resp2.Content.Headers.ContentType?.MediaType}");
Console.Write("  content:      \"");
await foreach (var delta in ReadSseDeltasAsync(resp2))
    Console.Write(delta);
Console.WriteLine("\"");
Console.WriteLine();

Console.WriteLine("Done.");
await app.StopAsync();

// ---- SSE reader ----
static async IAsyncEnumerable<string> ReadSseDeltasAsync(
    HttpResponseMessage response,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    using var stream = await response.Content.ReadAsStreamAsync(ct);
    using var reader = new StreamReader(stream);
    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        if (!line.StartsWith("data: ")) continue;
        var payload = line["data: ".Length..];
        if (payload == "[DONE]") yield break;
        using var chunk = JsonDocument.Parse(payload);
        var choices = chunk.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) continue;
        var delta = choices[0].GetProperty("delta");
        if (delta.TryGetProperty("content", out var content))
        {
            var text = content.GetString();
            if (text != null) yield return text;
        }
    }
}

// ---- scripted provider ----
sealed class ScriptedProvider : ICompletionProvider, IStreamingCompletionProvider
{
    public string ProviderName => "scripted";

    public Task<CompletionResponse> CompleteAsync(
        CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse("4", ModelId: "gpt-4o-mini"));

#pragma warning disable CS1998
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest req,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var word in new[] { "1", " 2", " 3" })
        {
            ct.ThrowIfCancellationRequested();
            yield return new CompletionUpdate(word);
        }
    }
#pragma warning restore CS1998
}
