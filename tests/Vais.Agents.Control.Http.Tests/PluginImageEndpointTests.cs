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
using Vais.Agents.Runtime.Plugins.Container;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Tests for <c>POST /v1/plugins/{name}/image</c> endpoint (IP-5).
/// </summary>
public sealed class PluginImageEndpointTests : IAsyncLifetime
{
    private IHost _hostWithReloader = null!;
    private IHost _hostNoReloader = null!;
    private HttpClient _clientWithReloader = null!;
    private HttpClient _clientNoReloader = null!;

    private sealed class StubContainerPluginReloader(ContainerPluginReloadResult result) : IContainerPluginReloader
    {
        public Task<ContainerPluginReloadResult> ReloadAsync(
            string pluginName, string newImage, CancellationToken ct = default)
            => Task.FromResult(result);
    }

    private static IHost BuildHost(IContainerPluginReloader? reloader) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    if (reloader is not null)
                        services.AddSingleton<IContainerPluginReloader>(reloader);
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapPluginControlPlane());
                });
            })
            .Build();

    public async Task InitializeAsync()
    {
        _hostWithReloader = BuildHost(new StubContainerPluginReloader(
            new ContainerPluginReloadResult("my-plugin", ContainerPluginReloadStatus.Success, null)));
        _hostNoReloader = BuildHost(null);

        await _hostWithReloader.StartAsync();
        await _hostNoReloader.StartAsync();

        _clientWithReloader = _hostWithReloader.GetTestClient();
        _clientWithReloader.BaseAddress ??= new Uri("http://localhost");
        _clientNoReloader = _hostNoReloader.GetTestClient();
        _clientNoReloader.BaseAddress ??= new Uri("http://localhost");
    }

    public async Task DisposeAsync()
    {
        _clientWithReloader.Dispose();
        _clientNoReloader.Dispose();
        await _hostWithReloader.StopAsync();
        await _hostNoReloader.StopAsync();
        _hostWithReloader.Dispose();
        _hostNoReloader.Dispose();
    }

    [Fact]
    public async Task Post_NoReloader_Returns503()
    {
        var response = await _clientNoReloader.PostAsJsonAsync(
            "/v1/plugins/my-plugin/image",
            new { image = "my-registry/my-plugin:1.0" });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Post_ReloaderSuccess_Returns200WithSuccessStatus()
    {
        var client = new AgentControlPlaneClient(_clientWithReloader);
        var response = await client.PushPluginImageAsync("my-plugin", "my-registry/my-plugin:1.0");

        response.Status.ToString().Should().Be("Success");
        response.PluginName.Should().Be("my-plugin");
        response.FailureUrn.Should().BeNull();
    }

    [Fact]
    public async Task Post_ReloaderNoSupervisor_Returns404()
    {
        var host = BuildHost(new StubContainerPluginReloader(
            new ContainerPluginReloadResult("unknown", ContainerPluginReloadStatus.NoSupervisor,
                "urn:vais:container:no-supervisor")));
        await host.StartAsync();
        var http = host.GetTestClient();
        http.BaseAddress ??= new Uri("http://localhost");

        var httpResponse = await http.PostAsJsonAsync("/v1/plugins/unknown/image",
            new { image = "my-registry/plugin:1.0" });

        httpResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await host.StopAsync();
        host.Dispose();
    }

    [Fact]
    public async Task Post_ReloaderHandlerTypeNameChanged_Returns422()
    {
        var host = BuildHost(new StubContainerPluginReloader(
            new ContainerPluginReloadResult("my-plugin", ContainerPluginReloadStatus.HandlerTypeNameChanged,
                "urn:vais:container:handler-type-name-changed")));
        await host.StartAsync();
        var http = host.GetTestClient();
        http.BaseAddress ??= new Uri("http://localhost");

        var httpResponse = await http.PostAsJsonAsync("/v1/plugins/my-plugin/image",
            new { image = "my-registry/plugin:2.0" });

        httpResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await host.StopAsync();
        host.Dispose();
    }
}
