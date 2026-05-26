// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// D-5: GET /v1/trajectories. Verifies the endpoint round-trips trajectory events through
/// the typed client; filters land on the in-memory store; returns 503 when no store is
/// registered.
/// </summary>
public sealed class TrajectoriesEndpointTests : IAsyncLifetime
{
    private IHost _host = null!;
    private IAgentControlPlaneClient _client = null!;
    private InMemoryInterceptorTeeStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new InMemoryInterceptorTeeStore(capacity: 100);
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IInterceptorTeeStore>(_store);
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapTrajectoryControlPlane());
                });
            })
            .StartAsync();

        var http = _host.GetTestClient();
        http.BaseAddress ??= new Uri("http://localhost");
        _client = new AgentControlPlaneClient(http);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task EmptyStore_ReturnsEmptyList()
    {
        var result = await _client.ListTrajectoriesAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnsNewestFirst_AndCarriesAllProjectedFields()
    {
        await _store.AppendAsync(Evt("a", t: DateTimeOffset.UtcNow.AddMinutes(-2), agent: "coord", concept: "fetch", transport: "south"));
        await _store.AppendAsync(Evt("b", t: DateTimeOffset.UtcNow, agent: "coord", concept: "deploy", transport: "south",
            outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Error, "Boom")));

        var events = await _client.ListTrajectoriesAsync();

        events.Should().HaveCount(2);
        events[0].EventId.Should().Be("b");
        events[0].ConceptName.Should().Be("deploy");
        events[0].Outcome!.Kind.Should().Be(TrajectoryOutcomeKind.Error);
        events[1].EventId.Should().Be("a");
    }

    [Fact]
    public async Task Filters_AreAppliedServerSide()
    {
        await _store.AppendAsync(Evt("a", agent: "coord", concept: "fetch", transport: "south"));
        await _store.AppendAsync(Evt("b", agent: "coord", concept: "deploy", transport: "south"));
        await _store.AppendAsync(Evt("c", agent: "other", concept: "fetch", transport: "north"));

        var coordOnly = await _client.ListTrajectoriesAsync(agent: "coord");
        coordOnly.Select(e => e.EventId).Should().BeEquivalentTo(["a", "b"]);

        var fetchOnly = await _client.ListTrajectoriesAsync(concept: "fetch");
        fetchOnly.Select(e => e.EventId).Should().BeEquivalentTo(["a", "c"]);

        var southOnly = await _client.ListTrajectoriesAsync(transport: "south");
        southOnly.Select(e => e.EventId).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task OutcomeFilter_IsParsedCaseInsensitively()
    {
        await _store.AppendAsync(Evt("ok", outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Ok)));
        await _store.AppendAsync(Evt("err", outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Error)));

        var errors = await _client.ListTrajectoriesAsync(outcome: "error");
        errors.Select(e => e.EventId).Should().Equal("err");
    }

    [Fact]
    public async Task LimitClampsBetween1And500()
    {
        for (var i = 0; i < 10; i++) await _store.AppendAsync(Evt($"e{i}"));

        // limit=0 → clamped to 1; limit=1000 → clamped to 500 (but only 10 in store).
        (await _client.ListTrajectoriesAsync(limit: 0)).Should().HaveCount(1);
        (await _client.ListTrajectoriesAsync(limit: 1000)).Should().HaveCount(10);
    }

    [Fact]
    public async Task Returns503_WhenStoreNotRegistered()
    {
        using var bareHost = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapTrajectoryControlPlane());
                });
            }).StartAsync();
        var bareHttp = bareHost.GetTestClient();
        bareHttp.BaseAddress ??= new Uri("http://localhost");
        var bareClient = new AgentControlPlaneClient(bareHttp);

        await FluentActions.Awaiting(() => bareClient.ListTrajectoriesAsync())
            .Should().ThrowAsync<AgentControlPlaneException>();
        await bareHost.StopAsync();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TrajectoryEvent Evt(
        string id,
        DateTimeOffset? t = null,
        string? agent = null,
        string? concept = null,
        string? transport = null,
        TrajectoryOutcome? outcome = null) =>
        new()
        {
            EventId = id,
            Timestamp = t ?? DateTimeOffset.UtcNow,
            EventName = "tool.call",
            Operation = OntologyOperation.Call,
            AgentId = agent,
            ConceptName = concept,
            Transport = transport,
            Outcome = outcome,
        };
}
