// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.ScriptRuntime;
using Vais.Agents.ScriptRuntime.Host;

var builder = WebApplication.CreateBuilder(args);

// Typed client: ScriptExecutor receives an HttpClient for the __callTool gateway callback.
builder.Services.AddHttpClient<ScriptExecutor>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Execute one code-mode script. Runs on a worker thread because Jint executes synchronously
// (and __callTool blocks on the gateway round-trip); the ScriptExecutor never throws for
// script-level failures — it returns a classified ScriptRunResponse.Error instead.
app.MapPost("/v1/script/run", async (ScriptRunRequest request, ScriptExecutor executor, CancellationToken ct) =>
{
    var response = await Task.Run(() => executor.Execute(request, ct), ct).ConfigureAwait(false);
    return Results.Ok(response);
});

app.Run();

/// <summary>Exposed so integration tests can drive the host via <c>WebApplicationFactory</c>.</summary>
public partial class Program;
