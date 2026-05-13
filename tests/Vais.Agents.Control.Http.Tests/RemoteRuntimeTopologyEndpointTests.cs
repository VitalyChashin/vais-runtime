// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// v0.34: GET /v1/runtimes endpoint — remote runtime topology discovery.
/// </summary>
public sealed class RemoteRuntimeTopologyEndpointTests : IAsyncLifetime
{
    // Local DTOs avoid CS0433: RuntimeListResponse / RuntimeInfo exist in both the server and
    // client packages under the same namespace. Using local mirror types lets ReadFromJsonAsync
    // resolve unambiguously while keeping identical JSON deserialization semantics.
    private sealed record RuntimeInfoDto(string Url, string IdentityMode);
    private sealed record RuntimeListDto(List<RuntimeInfoDto> Items);

    private IHost _hostWithTopology = null!;
    private IHost _hostWithoutTopology = null!;
    private HttpClient _httpWithTopology = null!;
    private HttpClient _httpWithoutTopology = null!;

    private static IRemoteRuntimeTopology BuildTopology(params (string Url, string Mode)[] entries)
    {
        var list = entries.Select(e => new RemoteRuntimeEntry(e.Url, e.Mode)).ToList();
        return new StubRemoteRuntimeTopology(list);
    }

    private sealed class StubRemoteRuntimeTopology(IReadOnlyList<RemoteRuntimeEntry> entries) : IRemoteRuntimeTopology
    {
        public IReadOnlyList<RemoteRuntimeEntry> GetEntries() => entries;
    }

    public async Task InitializeAsync()
    {
        var topology = BuildTopology(
            ("https://runtime-a.svc", "Forward"),
            ("https://runtime-b.svc", "ServiceAccount"));

        _hostWithTopology = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IRemoteRuntimeTopology>(topology);
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapRuntimeTopologyControlPlane());
                });
            })
            .StartAsync();

        _hostWithoutTopology = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapRuntimeTopologyControlPlane());
                });
            })
            .StartAsync();

        _httpWithTopology = _hostWithTopology.GetTestClient();
        _httpWithTopology.BaseAddress ??= new Uri("http://localhost");
        _httpWithoutTopology = _hostWithoutTopology.GetTestClient();
        _httpWithoutTopology.BaseAddress ??= new Uri("http://localhost");
    }

    public async Task DisposeAsync()
    {
        _httpWithTopology.Dispose();
        _httpWithoutTopology.Dispose();
        await _hostWithTopology.StopAsync();
        await _hostWithoutTopology.StopAsync();
        _hostWithTopology.Dispose();
        _hostWithoutTopology.Dispose();
    }

    [Fact]
    public async Task GetRuntimes_Returns_200_With_Configured_Runtimes()
    {
        using var response = await _httpWithTopology.GetAsync("/v1/runtimes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RuntimeListDto>();
        body.Should().NotBeNull();
        body!.Items.Should().HaveCount(2);
        body.Items.Should().ContainSingle(r => r.Url == "https://runtime-a.svc" && r.IdentityMode == "Forward");
        body.Items.Should().ContainSingle(r => r.Url == "https://runtime-b.svc" && r.IdentityMode == "ServiceAccount");
    }

    [Fact]
    public async Task GetRuntimes_Returns_200_With_Empty_Items_When_No_Topology_In_DI()
    {
        using var response = await _httpWithoutTopology.GetAsync("/v1/runtimes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RuntimeListDto>();
        body.Should().NotBeNull();
        body!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRuntimes_Does_Not_Expose_Sensitive_Fields()
    {
        using var response = await _httpWithTopology.GetAsync("/v1/runtimes");
        var raw = await response.Content.ReadAsStringAsync();

        raw.Should().NotContain("clientSecret", because: "OAuth client secrets must not leak");
        raw.Should().NotContain("clientId", because: "OAuth client IDs must not leak");
        raw.Should().NotContain("clientSecretRef", because: "secret refs must not leak");
        raw.Should().NotContain("tokenPath", because: "service-account token paths must not leak");
        raw.Should().NotContain("tokenExchangeEndpoint", because: "STS endpoints must not leak");
        raw.Should().NotContain("audience", because: "audience claims must not leak");
    }

    [Fact]
    public async Task GetRuntimes_Response_Contains_Only_Url_And_IdentityMode()
    {
        using var response = await _httpWithTopology.GetAsync("/v1/runtimes");
        var body = await response.Content.ReadFromJsonAsync<RuntimeListDto>();

        foreach (var item in body!.Items)
        {
            item.Url.Should().NotBeNullOrWhiteSpace();
            item.IdentityMode.Should().NotBeNullOrWhiteSpace();
        }
    }
}
