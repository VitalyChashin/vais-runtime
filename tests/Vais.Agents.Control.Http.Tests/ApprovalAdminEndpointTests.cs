// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control.InProcess;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// NB-8: the approval admin REST endpoints + the typed client. An operator lists
/// pending approvals and approves/rejects them; the held mutation is released by a
/// subsequent matching apply (covered at the gate level in ApprovalGateTests).
/// </summary>
public sealed class ApprovalAdminEndpointTests : IAsyncLifetime
{
    private IHost _host = null!;
    private IAgentControlPlaneClient _client = null!;
    private readonly InMemoryApprovalStore _store = new();

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IApprovalStore>(_store);
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapApprovalControlPlane());
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
    public async Task List_Then_Approve_RoundTrip()
    {
        var pending = await _store.CreatePendingAsync("ContainerPlugin", "p1", "hash-1", "alice");

        var listed = await _client.ListApprovalsAsync("pending");
        listed.Should().ContainSingle().Which.RequestId.Should().Be(pending.RequestId);

        var approved = await _client.ApproveAsync(pending.RequestId);
        approved!.Status.Should().Be(ApprovalStatus.Approved);
        approved.DecidedBy.Should().NotBeNull();

        (await _client.ListApprovalsAsync("pending")).Should().BeEmpty();
        (await _client.ListApprovalsAsync("approved")).Should().ContainSingle();
    }

    [Fact]
    public async Task Reject_Marks_Rejected()
    {
        var pending = await _store.CreatePendingAsync("Extension", "e1", "hash-2", "bob");

        var rejected = await _client.RejectAsync(pending.RequestId);

        rejected!.Status.Should().Be(ApprovalStatus.Rejected);
    }

    [Fact]
    public async Task Decide_Unknown_Returns_Null()
        => (await _client.ApproveAsync("does-not-exist")).Should().BeNull();
}
