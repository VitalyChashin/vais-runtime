// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using Xunit;

namespace Vais.Agents.Runtime.Host.Tests;

/// <summary>
/// Verifies that the Prometheus scraping endpoint is correctly wired when OTel is enabled.
/// Uses a minimal web host that mirrors the CompositionRoot pattern, without requiring
/// the full Orleans silo.
/// </summary>
public sealed class PrometheusEndpointTests : IAsyncLifetime
{
    private const string TestMeterName = "Vais.Agents.Runtime.Host.Tests.Prometheus";

    private IHost _host = null!;
    private HttpClient _http = null!;
    private Meter _meter = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddOpenTelemetry()
                        .WithMetrics(m => m
                            .AddMeter(TestMeterName)
                            .AddPrometheusExporter());
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapPrometheusScrapingEndpoint());
                });
            })
            .StartAsync();

        _meter = new Meter(TestMeterName);
        _http = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _meter.Dispose();
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Metrics_Returns_200_With_Prometheus_Content_Type()
    {
        using var response = await _http.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
    }

    [Fact]
    public async Task Metrics_Exports_Registered_Counter()
    {
        var counter = _meter.CreateCounter<long>("test_requests_total", description: "Test counter");
        counter.Add(42);

        using var response = await _http.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("test_requests_total", because: "registered counter must appear in the Prometheus scrape output");
        body.Should().Contain("# HELP test_requests_total", because: "Prometheus text format emits a HELP line for each metric family");
    }

    [Fact]
    public async Task Metrics_Endpoint_Is_At_Default_Path()
    {
        using var response = await _http.GetAsync("/metrics");
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }
}
