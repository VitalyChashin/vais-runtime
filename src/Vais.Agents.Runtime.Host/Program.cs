// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vais.Agents.Control.Http;
using Vais.Agents.Gateways.OpenAiCompat;
using Vais.Agents.Runtime.Host;
using Vais.Agents.Runtime.Plugins.Container;

var builder = WebApplication.CreateBuilder(args);

var options = RuntimeOptions.FromEnvironment();
options.EnsureValid();

// Fail fast on wiring mistakes: validate the whole DI graph at build (every registered service
// is constructable) and surface captive-dependency / scope violations instead of deferring them
// to the first request. On by default in Development under WebApplicationBuilder; we enable it in
// every environment so the production container catches latent misconfiguration at boot.
builder.Host.UseDefaultServiceProvider((_, o) =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.Host.UseOrleans(silo => CompositionRoot.ConfigureSilo(silo, options));
CompositionRoot.ConfigureServices(builder.Services, options, builder.Configuration);

var app = builder.Build();

// Startup self-check: resolve every always-on critical contract once so a missing registration
// fails the boot with a clear message instead of a first-invoke ManifestInstantiationException.
// Covers the service-located deps (IAgentRuntime, IBackgroundAgentTracker, …) resolved during
// translation/activation that ValidateOnBuild cannot see.
CriticalRuntimeContracts.Verify(app.Services);

app.Logger.LogInformation(
    "Vais.Agents runtime starting — mode={Mode} clustering={Clustering} persistence={Persistence} pubsub={PubSub} opa={Opa} otel={Otel} langfuse={Langfuse} jwt={Jwt} bootManifests={BootManifests}",
    options.Mode,
    options.Mode == "clustered" ? options.ClusteringBackend : "n/a",
    options.Mode == "localhost" ? options.LocalhostPersistence.ToString().ToLowerInvariant() : "n/a",
    options.Mode == "localhost" ? options.LocalhostPubSubPersistence.ToString().ToLowerInvariant() : "n/a",
    string.IsNullOrWhiteSpace(options.OpaBaseUrl) ? "disabled (AllowAll)" : $"enabled ({options.OpaFailMode})",
    (options.OtelEndpoint, options.OtelConsole) switch
    {
        (null or "", false) => "disabled",
        (null or "", true) => "console",
        _ => "otlp",
    },
    string.IsNullOrWhiteSpace(options.LangfuseProject) ? "disabled" : "enabled",
    string.IsNullOrWhiteSpace(options.JwtAuthority)
        ? "disabled"
        : options.UseSaPrincipalMapper ? $"enabled+sa-mapper ({options.JwtAuthority})" : $"enabled ({options.JwtAuthority})",
    string.IsNullOrWhiteSpace(options.BootManifestsDirectory) ? "disabled" : options.BootManifestsDirectory);

app.Logger.LogInformation(
    "Vais.Agents observability stores — run-store={RunStore} agent-run-store={AgentRunStore} gateway-event-store={GatewayEventStore} mcp-event-store={McpEventStore} mcp-gateway-event-store={McpGatewayEventStore} agent-log-sink={AgentLogSink}",
    FormatStore(options.RunStoreConnection),
    FormatStore(options.AgentRunStoreConnection),
    FormatEventStore(options.GatewayEventStoreConnection, "gateway", options.GatewayId),
    FormatEventStore(options.McpEventStoreConnection, "server", options.McpServerId),
    FormatEventStore(options.McpGatewayEventStoreConnection, "gateway", options.McpGatewayId),
    $"enabled(buffer={options.AgentLogBufferLines} lines)");

static string FormatStore(string? conn) =>
    !string.IsNullOrWhiteSpace(conn)
        ? $"enabled({ConnectionStringDisplay.RedactPostgres(conn)})"
        : "disabled";

static string FormatEventStore(string? conn, string idKey, string? id) =>
    !string.IsNullOrWhiteSpace(conn)
        ? $"enabled({idKey}={id ?? "default"}, {ConnectionStringDisplay.RedactPostgres(conn)})"
        : "disabled";

app.UseAgentControlPlaneIdempotency();

var corsDisabled = string.Equals(options.CorsOrigins, "disabled", StringComparison.OrdinalIgnoreCase);
if (!corsDisabled && (options.Mode == "localhost" || !string.IsNullOrWhiteSpace(options.CorsOrigins)))
    app.UseCors();

if (!string.IsNullOrWhiteSpace(options.JwtAuthority))
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAgentControlPlanePrincipalMapping();
}

app.MapAgentControlPlane();
app.MapAgentControlPlaneOpenApi();
app.MapOpenAiCompat();

if (!string.IsNullOrWhiteSpace(options.PythonPluginsDirectory) ||
    !string.IsNullOrWhiteSpace(options.ContainerPluginsDirectory))
    app.MapContainerGatewayEndpoints();

app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
    ResponseWriter = WriteReadyzJsonAsync,
});

static async Task WriteReadyzJsonAsync(HttpContext ctx, HealthReport report)
{
    ctx.Response.ContentType = "application/json; charset=utf-8";
    await using var writer = new Utf8JsonWriter(ctx.Response.Body, new JsonWriterOptions { Indented = false });
    writer.WriteStartObject();
    writer.WriteString("status", report.Status.ToString());
    writer.WriteStartObject("results");
    foreach (var (name, entry) in report.Entries)
    {
        writer.WriteStartObject(name);
        writer.WriteString("status", entry.Status.ToString());
        if (entry.Description is not null)
            writer.WriteString("description", entry.Description);
        if (entry.Data.Count > 0)
        {
            writer.WriteStartObject("data");
            foreach (var (k, v) in entry.Data)
                writer.WriteString(k, v?.ToString() ?? "");
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
    writer.WriteEndObject();
    writer.WriteEndObject();
    await writer.FlushAsync();
}

if (!string.IsNullOrWhiteSpace(options.OtelEndpoint) || options.OtelConsole)
    app.MapPrometheusScrapingEndpoint();

app.Run();

/// <summary>
/// Marker for <c>WebApplicationFactory&lt;TEntryPoint&gt;</c>.
/// </summary>
public partial class Program;
