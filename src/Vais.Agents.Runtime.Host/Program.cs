// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Vais.Agents.Control.Http;
using Vais.Agents.Runtime.Host;

var builder = WebApplication.CreateBuilder(args);

var options = RuntimeOptions.FromEnvironment();
options.EnsureValid();

builder.Host.UseOrleans(silo => CompositionRoot.ConfigureSilo(silo, options));
CompositionRoot.ConfigureServices(builder.Services, options, builder.Configuration);

var app = builder.Build();

app.Logger.LogInformation(
    "Vais.Agents runtime starting — mode={Mode} clustering={Clustering} opa={Opa} otel={Otel} langfuse={Langfuse} jwt={Jwt} bootManifests={BootManifests}",
    options.Mode,
    options.Mode == "clustered" ? options.ClusteringBackend : "n/a",
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

app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
});

if (!string.IsNullOrWhiteSpace(options.OtelEndpoint) || options.OtelConsole)
    app.MapPrometheusScrapingEndpoint();

app.Run();

/// <summary>
/// Marker for <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
public partial class Program;
