// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control.Kubernetes;

var builder = WebApplication.CreateBuilder(args);

// Bind operator options from config — reads from appsettings.json + env
// vars prefixed with Vais__KubernetesOperator__* per .NET convention.
builder.Services.AddAgentKubernetesOperator(opts =>
{
    builder.Configuration.GetSection("Vais:KubernetesOperator").Bind(opts);
});

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(8080);
});

var app = builder.Build();

// Health + readiness probes consumed by the Helm chart's Deployment.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

app.Run();

/// <summary>
/// Marker class for WebApplicationFactory / integration tests that need
/// a stable entry-point reference. Kept public-minimal; the actual
/// composition lives at the top of this file.
/// </summary>
public partial class Program;
