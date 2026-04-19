// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Observability.Langfuse;
using Vais.Agents.Observability.OpenTelemetry;

// -----------------------------------------------------------------------------
// ObservabilityOtelConsole — wires AddAgenticInstrumentation on both Tracer
// and Meter providers with console exporters, plus AddLangfuseEnrichment.
// Drives a turn through a scripted provider and prints the emitted span +
// histogram metrics to the console.
// -----------------------------------------------------------------------------

using var tracer = Sdk.CreateTracerProviderBuilder()
    .AddAgenticInstrumentation()
    .AddConsoleExporter()
    .Build();

using var meter = Sdk.CreateMeterProviderBuilder()
    .AddAgenticInstrumentation()
    .AddConsoleExporter((exporterOptions, metricReaderOptions) =>
    {
        // Force a metric flush every second so the export is visible in short runs.
        metricReaderOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1_000;
    })
    .Build();

var accessor = new AsyncLocalAgentContextAccessor();
using var scope = accessor.Push(new AgentContext(
    UserId: "alice@example.com",
    TenantId: "acme",
    CorrelationId: Guid.NewGuid().ToString("N"),
    AgentName: "observability-demo"));

var services = new ServiceCollection();
services.AddSingleton<IAgentContextAccessor>(accessor);   // LangfuseEnrichmentFilter depends on it
services.AddAgenticOpenTelemetrySink();
services.AddLangfuseEnrichment(new LangfuseEnrichmentOptions
{
    DefaultTags = new[] { "vais-agents", "observability-sample" },
});
services.AddSingleton<ICompletionProvider, EchoingProvider>();

var sp = services.BuildServiceProvider();

var agent = new StatefulAiAgent(
    sp.GetRequiredService<ICompletionProvider>(),
    new StatefulAgentOptions
    {
        AgentName = "observability-demo",
        UsageSink = sp.GetRequiredService<IUsageSink>(),
        Filters = new[] { sp.GetRequiredService<LangfuseEnrichmentFilter>() },
        ContextAccessor = accessor,
    });

Console.WriteLine(await agent.AskAsync("Say hi."));
Console.WriteLine();
Console.WriteLine("(scroll up for the OTel span + metric exports)");

// Let the metric exporter flush before we dispose.
await Task.Delay(1_500);

sealed class EchoingProvider : ICompletionProvider
{
    public string ProviderName => "echoing";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(
            "Hi! (telemetry emitted in parallel)",
            ModelId: "echo-model",
            PromptTokens: 8,
            CompletionTokens: 6));
}
