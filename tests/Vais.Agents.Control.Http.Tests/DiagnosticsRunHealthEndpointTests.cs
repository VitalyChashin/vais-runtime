// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// DM-5/DM-10 — Part 2c REST endpoints. Asserts the routes call the SAME services the
/// MCP handlers use (IRunHealthAggregator, IFailureSearchService), so CLI/REST and MCP
/// cannot drift (advisor's "share the backing call" constraint).
/// </summary>
public sealed class DiagnosticsRunHealthEndpointTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _client = null!;
    private StubAggregator _aggregator = null!;
    private StubSearch _search = null!;

    public async Task InitializeAsync()
    {
        _aggregator = new StubAggregator();
        _search = new StubSearch();

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IRunHealthAggregator>(_aggregator);
                    services.AddSingleton<IFailureSearchService>(_search);
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapDiagnosticsControlPlane());
                });
            })
            .StartAsync();
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // ── GET /v1/run-health ───────────────────────────────────────────────────

    [Fact]
    public async Task RunHealth_ReturnsAggregatorRows()
    {
        _aggregator.RunSummaries =
        [
            new RunHealthListItem("run-1", "degraded", 2, new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero)),
            new RunHealthListItem("run-2", "failed", 5, new DateTimeOffset(2026, 5, 31, 13, 0, 0, TimeSpan.Zero)),
        ];

        var resp = await _client.GetAsync("/v1/run-health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task RunHealth_LevelFailed_MapsToError()
    {
        _aggregator.RunSummaries = [];
        await _client.GetAsync("/v1/run-health?level=failed");
        _aggregator.LastMinLevel.Should().Be(FailureLevel.Error);
    }

    [Fact]
    public async Task RunHealth_LevelDegraded_MapsToWarning()
    {
        _aggregator.RunSummaries = [];
        await _client.GetAsync("/v1/run-health?level=degraded");
        _aggregator.LastMinLevel.Should().Be(FailureLevel.Warning);
    }

    [Fact]
    public async Task RunHealth_InvalidLevel_Returns400()
    {
        var resp = await _client.GetAsync("/v1/run-health?level=purple");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RunHealth_InvalidSince_Returns400()
    {
        var resp = await _client.GetAsync("/v1/run-health?since=not-a-date");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── GET /v1/run-health/signals ───────────────────────────────────────────

    [Fact]
    public async Task RunHealthSignals_PassesQueryToSearch()
    {
        _search.Rows = [];
        await _client.GetAsync("/v1/run-health/signals?concept=McpToolError&agentName=my-agent&limit=25");
        _search.LastQuery.Should().NotBeNull();
        _search.LastQuery!.ConceptName.Should().Be("McpToolError");
        _search.LastQuery!.AgentName.Should().Be("my-agent");
        _search.LastQuery!.Limit.Should().Be(25);
    }

    [Fact]
    public async Task RunHealthSignals_ReturnsServiceRows_WithAttributionPath()
    {
        _search.Rows =
        [
            new FailureSearchResult(
                RunId: "run-A",
                ConceptName: "McpToolError/AuthExpired",
                AttributionPath: "confluence-agent/confluence-mcp/confluence_search",
                Source: "confluence_search",
                Level: "warning",
                ErrorType: "Unauthorized",
                At: new DateTimeOffset(2026, 5, 31, 12, 0, 0, TimeSpan.Zero)),
        ];

        var resp = await _client.GetAsync("/v1/run-health/signals?concept=McpToolError");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("items")[0];
        row.GetProperty("runId").GetString().Should().Be("run-A");
        row.GetProperty("conceptName").GetString().Should().Be("McpToolError/AuthExpired");
        row.GetProperty("attributionPath").GetString().Should().Be("confluence-agent/confluence-mcp/confluence_search");
    }

    [Fact]
    public async Task RunHealthSignals_NoSearchService_Returns503()
    {
        // Build a host without IFailureSearchService.
        using var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s => s.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapDiagnosticsControlPlane());
                });
            })
            .StartAsync();
        using var client = host.GetTestClient();

        var resp = await client.GetAsync("/v1/run-health/signals");
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ── Stubs ────────────────────────────────────────────────────────────────

    private sealed class StubAggregator : IRunHealthAggregator
    {
        public IReadOnlyList<RunHealthListItem> RunSummaries { get; set; } = [];
        public FailureLevel? LastMinLevel { get; set; }

        public Task<RunHealth> GetRunHealthAsync(string rootRunId, CancellationToken ct = default)
            => Task.FromResult(new RunHealth(rootRunId, RunHealthLevel.Healthy, [], []));

        public Task<IReadOnlyList<RunHealthListItem>> ListDegradedRunsAsync(
            FailureLevel minLevel = FailureLevel.Warning,
            DateTimeOffset? since = null,
            int limit = 50,
            CancellationToken ct = default)
        {
            LastMinLevel = minLevel;
            return Task.FromResult(RunSummaries);
        }
    }

    private sealed class StubSearch : IFailureSearchService
    {
        public IReadOnlyList<FailureSearchResult> Rows { get; set; } = [];
        public FailureSearchQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<FailureSearchResult>> SearchAsync(
            FailureSearchQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(Rows);
        }
    }
}
