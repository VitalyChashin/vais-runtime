// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Runtime.Plugins;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// v0.27: GET /v1/plugins endpoint — list loaded plugins.
/// </summary>
public sealed class PluginControlPlaneEndpointTests : IAsyncLifetime
{
    private IHost _hostWithPlugins = null!;
    private IHost _hostWithoutPlugins = null!;
    private HttpClient _httpWithPlugins = null!;
    private HttpClient _httpWithoutPlugins = null!;

    private static PluginDescriptor MakeDescriptor(string name, params string[] handlers) =>
        new(
            Name: name,
            AssemblyPath: $"/plugins/{name}/{name}.dll",
            TargetApiVersion: "0.27",
            Handlers: handlers,
            LoadedViaAttribute: true,
            LoadContext: AssemblyLoadContext.Default);

    private static IPluginHandlerRegistry MakeRegistry(params PluginDescriptor[] descriptors)
    {
        return new StubPluginHandlerRegistry(descriptors);
    }

    private sealed class StubPluginHandlerRegistry(IReadOnlyList<PluginDescriptor> plugins) : IPluginHandlerRegistry
    {
        public void Register(IAgentHandlerFactory factory, string ownerPluginName) => throw new NotSupportedException();
        public bool TryGet(string handlerTypeName, out IAgentHandlerFactory? factory) { factory = null; return false; }
        public IReadOnlyCollection<string> HandlerTypeNames => Array.Empty<string>();
        public IReadOnlyCollection<PluginDescriptor> Plugins => plugins.ToArray();
    }

    public async Task InitializeAsync()
    {
        var registry = MakeRegistry(
            MakeDescriptor("alpha", "MyApp.AlphaHandler"),
            MakeDescriptor("beta", "MyApp.BetaOne", "MyApp.BetaTwo"));

        _hostWithPlugins = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IPluginHandlerRegistry>(registry);
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

        _hostWithoutPlugins = await new HostBuilder()
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
                    app.UseEndpoints(e => e.MapPluginControlPlane());
                });
            })
            .StartAsync();

        _httpWithPlugins = _hostWithPlugins.GetTestClient();
        _httpWithPlugins.BaseAddress ??= new Uri("http://localhost");
        _httpWithoutPlugins = _hostWithoutPlugins.GetTestClient();
        _httpWithoutPlugins.BaseAddress ??= new Uri("http://localhost");
    }

    public async Task DisposeAsync()
    {
        _httpWithPlugins.Dispose();
        _httpWithoutPlugins.Dispose();
        await _hostWithPlugins.StopAsync();
        await _hostWithoutPlugins.StopAsync();
        _hostWithPlugins.Dispose();
        _hostWithoutPlugins.Dispose();
    }

    [Fact]
    public async Task List_Returns_200_With_Registered_Plugins()
    {
        var client = new AgentControlPlaneClient(_httpWithPlugins);
        var body = await client.ListPluginsAsync();

        body.Items.Should().HaveCount(2);

        var alpha = body.Items.Single(p => p.Name == "alpha");
        alpha.AssemblyPath.Should().Be("/plugins/alpha/alpha.dll");
        alpha.TargetApiVersion.Should().Be("0.27");
        alpha.Handlers.Should().ContainSingle().Which.Should().Be("MyApp.AlphaHandler");
        alpha.LoadedViaAttribute.Should().BeTrue();

        var beta = body.Items.Single(p => p.Name == "beta");
        beta.Handlers.Should().BeEquivalentTo(new[] { "MyApp.BetaOne", "MyApp.BetaTwo" });
    }

    [Fact]
    public async Task List_Returns_200_With_Empty_Items_When_No_Registry_In_DI()
    {
        var client = new AgentControlPlaneClient(_httpWithoutPlugins);
        var body = await client.ListPluginsAsync();

        body.Items.Should().BeEmpty();
    }


}
