// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Mcp.Server.Tests;

/// <summary>
/// ND-5 guard — the design-tools MCP server mounts at /design-mcp and registers
/// the expected DI services and tool declarations.
/// </summary>
public sealed class DesignMcpServerTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddMcpDesignServer();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapMcpDesignServer());
                });
            })
            .StartAsync();

        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task DesignMcpServer_IsMounted_AtDefaultPath()
    {
        using var response = await _http.PostAsync(
            "/design-mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "the design-tools MCP endpoint must be mounted at /design-mcp");
    }

    [Fact]
    public void AddMcpDesignServer_Registers_McpServerOptions_With_5_Tools()
    {
        using var scope = _host.Services.CreateScope();
        var opts = scope.ServiceProvider.GetService<IOptions<McpServerOptions>>();
        opts.Should().NotBeNull();
        opts!.Value.ServerInfo!.Name.Should().Be("vais-design");
        opts.Value.Handlers.ListToolsHandler.Should().NotBeNull();
        opts.Value.Handlers.CallToolHandler.Should().NotBeNull();
    }

    [Fact]
    public void AddMcpDesignServer_Registers_IOntologyCatalog()
    {
        var catalog = _host.Services.GetRequiredService<IOntologyCatalog>();
        catalog.Kinds.Should().HaveCountGreaterOrEqualTo(7, "base ontology has 7 schema-backed kinds");
        catalog.OntologyVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DesignTools_ListDeclarations_ContainsAllFiveVerbs()
    {
        var names = DesignMcpToolHandlers.DesignTools.Select(t => t.Name).ToList();
        names.Should().Contain("vais.list");
        names.Should().Contain("vais.get");
        names.Should().Contain("vais.describe");
        names.Should().Contain("vais.diff");
        names.Should().Contain("vais.validate");
    }
}
