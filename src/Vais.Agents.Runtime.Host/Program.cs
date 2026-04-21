// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Vais.Agents.Control.Http;
using Vais.Agents.Runtime.Host;

var builder = WebApplication.CreateBuilder(args);

var options = RuntimeOptions.FromEnvironment();
options.EnsureValid();

builder.Host.UseOrleans(silo => CompositionRoot.ConfigureSilo(silo, options));
CompositionRoot.ConfigureServices(builder.Services, options);

var app = builder.Build();

app.Logger.LogInformation(
    "Vais.Agents runtime starting — mode={Mode} clustering={Clustering} opa={Opa} otel={Otel} langfuse={Langfuse}",
    options.Mode,
    options.Mode == "clustered" ? options.ClusteringBackend : "n/a",
    string.IsNullOrWhiteSpace(options.OpaBaseUrl) ? "disabled (AllowAll)" : $"enabled ({options.OpaFailMode})",
    (options.OtelEndpoint, options.OtelConsole) switch
    {
        (null or "", false) => "disabled",
        (null or "", true) => "console",
        _ => "otlp",
    },
    string.IsNullOrWhiteSpace(options.LangfuseProject) ? "disabled" : "enabled");

app.UseAgentControlPlaneIdempotency();
app.MapAgentControlPlane();
app.MapAgentControlPlaneOpenApi();

app.MapHealthChecks("/healthz");
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
});

app.Run();

/// <summary>
/// Marker for <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>.
/// </summary>
public partial class Program;
