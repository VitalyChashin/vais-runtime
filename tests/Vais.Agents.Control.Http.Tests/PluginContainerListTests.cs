// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
/// Tests that <c>GET /v1/plugins</c> includes container plugins when
/// <see cref="IContainerPluginHost"/> is registered in DI (IP-5).
/// </summary>
public sealed class PluginContainerListTests : IAsyncLifetime
{
    private IHost _hostWithContainer = null!;
    private HttpClient _httpWithContainer = null!;

    private sealed class StubContainerPluginHost(params LoadedContainerPlugin[] plugins) : IContainerPluginHost
    {
        public IReadOnlyList<LoadedContainerPlugin> LoadedPlugins => plugins;
    }

    public async Task InitializeAsync()
    {
        var containerHost = new StubContainerPluginHost(
            new LoadedContainerPlugin(
                Name: "sgr-analyst",
                Image: "my-registry/sgr-analyst:1.2.0",
                HandlerTypeName: "SgrAnalyst",
                TargetApiVersion: "1.0.0",
                Status: ContainerPluginStatus.Ready));

        _hostWithContainer = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IContainerPluginHost>(containerHost);
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapPluginControlPlane());
                });
            })
            .StartAsync();

        _httpWithContainer = _hostWithContainer.GetTestClient();
        _httpWithContainer.BaseAddress ??= new Uri("http://localhost");
    }

    public async Task DisposeAsync()
    {
        _httpWithContainer.Dispose();
        await _hostWithContainer.StopAsync();
        _hostWithContainer.Dispose();
    }

    [Fact]
    public async Task List_ContainerPluginsIncluded_WhenHostRegistered()
    {
        var client = new AgentControlPlaneClient(_httpWithContainer);
        var response = await client.ListPluginsAsync();

        response.Items.Should().ContainSingle(p => p.Name == "sgr-analyst");
    }

    [Fact]
    public async Task List_ContainerPlugin_HasContainerKind()
    {
        var client = new AgentControlPlaneClient(_httpWithContainer);
        var response = await client.ListPluginsAsync();

        var plugin = response.Items.Single(p => p.Name == "sgr-analyst");
        plugin.Kind.ToString().Should().Be("Container");
    }

    [Fact]
    public async Task List_ContainerPlugin_ImageFieldPopulated()
    {
        var client = new AgentControlPlaneClient(_httpWithContainer);
        var response = await client.ListPluginsAsync();

        var plugin = response.Items.Single(p => p.Name == "sgr-analyst");
        plugin.Image.Should().Be("my-registry/sgr-analyst:1.2.0");
    }
}
