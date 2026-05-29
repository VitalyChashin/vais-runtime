// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vais.Agents.ScriptRuntime;
using Vais.Agents.ScriptRuntime.Host;

var builder = WebApplication.CreateBuilder(args);

// Typed client: ScriptExecutor receives an HttpClient for the __callTool gateway callback.
builder.Services.AddHttpClient<ScriptExecutor>();

// Export the per-script spans (ScriptExecutor.ActivitySource) via OTLP when an endpoint is
// configured — OTEL_EXPORTER_OTLP_* env is injected by the runtime / compose / Helm. Each span
// anchors to the run_code span via the incoming traceparent, so the script execution nests under
// the agent turn in Langfuse. No endpoint → no exporter (the ActivitySource is then a no-op).
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("vais-script-runtime"))
        .WithTracing(t => t
            .AddSource(ScriptExecutor.ActivitySource.Name)
            .AddOtlpExporter());
}

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
