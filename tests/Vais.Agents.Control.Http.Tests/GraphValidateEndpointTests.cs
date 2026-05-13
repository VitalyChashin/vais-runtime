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
using Vais.Agents.Core;
using Vais.Agents.Runtime.Plugins;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// v0.38: POST /v1/graphs/validate — stateless dry-run validation endpoint.
/// Covers structural errors, malformed JSON, Code-kind handler registry checks,
/// Agent-kind agent registry checks, and the no-registry DI skip path.
/// </summary>
public sealed class GraphValidateEndpointTests : IAsyncLifetime
{
    private IHost _hostBare = null!;
    private HttpClient _httpBare = null!;

    public async Task InitializeAsync()
    {
        _hostBare = await BuildHostAsync();
        _httpBare = _hostBare.GetTestClient();
        _httpBare.BaseAddress ??= new Uri("http://localhost");
    }

    public async Task DisposeAsync()
    {
        _httpBare.Dispose();
        await _hostBare.StopAsync();
        _hostBare.Dispose();
    }

    // ── Structural / format checks ────────────────────────────────────────────

    [Fact]
    public async Task Validate_ValidManifest_ReturnsValidTrue()
    {
        var result = await new AgentControlPlaneClient(_httpBare).ValidateGraphAsync(EndKindManifest());

        result.Valid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_StructurallyInvalid_Returns200ValidFalse()
    {
        // entry references a node that is not in the nodes list → AgentManifestValidationException
        var badManifest = new AgentGraphManifest(
            Id: "bad-graph", Version: "1.0", Entry: "nonexistent",
            Nodes: new[] { new GraphNode("start", "End") },
            Edges: Array.Empty<GraphEdge>());

        var result = await new AgentControlPlaneClient(_httpBare).ValidateGraphAsync(badManifest);

        result.Valid.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Validate_MalformedJson_Returns400()
    {
        var content = new StringContent("not valid json at all", Encoding.UTF8, "application/json");
        using var resp = await _httpBare.PostAsync("/v1/graphs/validate", content);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Code-kind handler registry checks ─────────────────────────────────────

    [Fact]
    public async Task Validate_CodeKind_HandlerRegistered_ReturnsValidTrue()
    {
        const string handler = "MyApp.CodeHandler";
        using var host = await BuildHostAsync(pluginRegistry: new StubPluginHandlerRegistry(new[] { handler }));
        using var http = MakeClient(host);

        var result = await new AgentControlPlaneClient(http).ValidateGraphAsync(CodeKindManifest(handler));

        result.Valid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_CodeKind_HandlerNotRegistered_ReturnsValidFalse()
    {
        const string handler = "MyApp.CodeHandler";
        using var host = await BuildHostAsync(pluginRegistry: new StubPluginHandlerRegistry(new[] { "MyApp.OtherHandler" }));
        using var http = MakeClient(host);

        var result = await new AgentControlPlaneClient(http).ValidateGraphAsync(CodeKindManifest(handler));

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains(handler));
    }

    // ── Agent-kind agent registry checks ─────────────────────────────────────

    [Fact]
    public async Task Validate_AgentKind_AgentRegistered_ReturnsValidTrue()
    {
        const string agentId = "my-agent";
        const string version = "1.0";
        var agentRegistry = new InMemoryAgentRegistry();
        agentRegistry.Register(new AgentManifest(agentId, version,
            new AgentHandlerRef("MyApp.AgentHandler"),
            Array.Empty<ProtocolBinding>(),
            Array.Empty<ToolRef>()));
        using var host = await BuildHostAsync(agentRegistry: agentRegistry);
        using var http = MakeClient(host);

        var result = await new AgentControlPlaneClient(http).ValidateGraphAsync(AgentKindManifest(agentId, version));

        result.Valid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Validate_AgentKind_AgentAbsent_ReturnsValidFalse()
    {
        const string agentId = "absent-agent";
        using var host = await BuildHostAsync(agentRegistry: new InMemoryAgentRegistry());
        using var http = MakeClient(host);

        var result = await new AgentControlPlaneClient(http).ValidateGraphAsync(AgentKindManifest(agentId));

        result.Valid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.Contains(agentId));
    }

    // ── No registries in DI — runtime checks are skipped ─────────────────────

    [Fact]
    public async Task Validate_NoRegistriesInDI_SkipsRuntimeChecks_ReturnsValidTrue()
    {
        // _hostBare has no IPluginHandlerRegistry or IAgentRegistry — runtime checks skip
        var result = await new AgentControlPlaneClient(_httpBare).ValidateGraphAsync(CodeKindManifest("Any.UnknownHandler"));

        result.Valid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<IHost> BuildHostAsync(
        IPluginHandlerRegistry? pluginRegistry = null,
        IAgentRegistry? agentRegistry = null)
        => new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    if (pluginRegistry is not null)
                        services.AddSingleton(pluginRegistry);
                    if (agentRegistry is not null)
                        services.AddSingleton(agentRegistry);
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapGraphControlPlane());
                });
            })
            .StartAsync();

    private static HttpClient MakeClient(IHost host)
    {
        var http = host.GetTestClient();
        http.BaseAddress ??= new Uri("http://localhost");
        return http;
    }

    private static AgentGraphManifest EndKindManifest(string id = "test-graph") =>
        new(Id: id, Version: "1.0", Entry: "start",
            Nodes: new[] { new GraphNode("start", "End") },
            Edges: Array.Empty<GraphEdge>());

    private static AgentGraphManifest CodeKindManifest(string handlerTypeName, string id = "code-graph") =>
        new(Id: id, Version: "1.0", Entry: "step",
            Nodes: new[] { new GraphNode("step", "Code", HandlerRef: new GraphHandlerRef(handlerTypeName)) },
            Edges: Array.Empty<GraphEdge>());

    private static AgentGraphManifest AgentKindManifest(string agentId, string version = "1.0", string id = "agent-graph") =>
        new(Id: id, Version: "1.0", Entry: "step",
            Nodes: new[] { new GraphNode("step", "Agent", Ref: new GraphAgentRef(agentId, version)) },
            Edges: Array.Empty<GraphEdge>());

    private sealed class StubPluginHandlerRegistry(IReadOnlyCollection<string> handlerNames) : IPluginHandlerRegistry
    {
        public void Register(IAgentHandlerFactory factory, string ownerPluginName) => throw new NotSupportedException();
        public bool TryGet(string handlerTypeName, out IAgentHandlerFactory? factory) { factory = null; return false; }
        public IReadOnlyCollection<string> HandlerTypeNames => handlerNames;
        public IReadOnlyCollection<PluginDescriptor> Plugins => Array.Empty<PluginDescriptor>();
    }
}
