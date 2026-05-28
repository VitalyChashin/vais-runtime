// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// v0.6 PR 3: HTTP control plane end-to-end. Spins up a minimal-API TestServer
/// with the in-process lifecycle manager + in-memory registry + a fake completion
/// provider. Exercises the shipped REST surface via both raw <see cref="HttpClient"/>
/// and the typed <see cref="AgentControlPlaneClient"/>.
/// </summary>
public sealed class AgentControlPlaneHttpTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;
    private IAgentControlPlaneClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("hello from fake")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>()));
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapAgentControlPlane());
                });
            })
            .StartAsync();

        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");
        _client = new AgentControlPlaneClient(_http);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Healthz_Returns_200()
    {
        using var response = await _http.GetAsync("/v1/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_List_Query_Invoke_Evict_RoundTrip_Via_Typed_Client()
    {
        var manifest = new AgentManifest(
            "support", "1.0",
            new AgentHandlerRef("declarative"),
            new[] { new ProtocolBinding("Http") },
            Array.Empty<ToolRef>());

        var handle = await _client.CreateAsync(manifest);
        handle.AgentId.Should().Be("support");

        var list = await _client.ListAsync();
        list.Should().ContainSingle().Which.Id.Should().Be("support");

        var q = await _client.QueryAsync("support");
        q.Should().NotBeNull();
        q!.Handle.Version.Should().Be("1.0");
        q.Status.Should().Be(AgentStatus.Idle);

        var result = await _client.InvokeAsync("support", new AgentInvocationRequest("hi"));
        result.Text.Should().Be("hello from fake");

        await _client.EvictAsync("support");
        (await _client.QueryAsync("support")).Should().BeNull();
    }

    [Fact]
    public async Task Create_Accepts_Json_Body_Via_Raw_HttpClient()
    {
        var json = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "echo", "version": "1.0" }
        }
        """;
        using var response = await _http.PostAsync("/v1/agents",
            new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"agentId\":\"echo\"");
    }

    [Fact]
    public async Task Invalid_Manifest_Returns_Problem_Details_With_Manifest_Invalid_Type()
    {
        var bad = """
        { "apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"Bad","version":"nope"} }
        """;
        using var response = await _http.PostAsync("/v1/agents",
            new StringContent(bad, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString()
            .Should().Be(ProblemDetailsMapping.ManifestInvalidType);
    }

    [Fact]
    public async Task Unknown_Agent_Query_Returns_404()
    {
        using var response = await _http.GetAsync("/v1/agents/ghost");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invoke_Unknown_Agent_Returns_404_With_AgentHandleNotFound_URN()
    {
        // Verifies the AgentHandleNotFoundException → urn:vais-agents:agent-handle-not-found
        // mapping introduced alongside the lazy-hydrate fix. POSTing to /v1/agents/{ghost}/invoke
        // surfaces the typed 404 so external clients can distinguish "agent doesn't exist"
        // from generic 500s.
        var body = new StringContent("""{"text":"hi"}""", Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/v1/agents/ghost/invoke", body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString()
            .Should().Be(ProblemDetailsMapping.AgentHandleNotFoundType);
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(404);
        doc.RootElement.GetProperty("detail").GetString()
            .Should().Contain("ghost", "the error detail must name the missing agent");
    }

    [Fact]
    public async Task Policy_Deny_Surfaces_Via_Problem_Details_403()
    {
        // Rebuild host with a denying policy for Create only.
        await _host.StopAsync();
        _host.Dispose();
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("ok")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentPolicyEngine>(new DenyCreatePolicy());
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>(),
                        sp.GetService<IAgentPolicyEngine>()));
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapAgentControlPlane());
                });
            })
            .StartAsync();
        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");

        var json = """
        { "apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"blocked","version":"1.0"} }
        """;
        using var response = await _http.PostAsync("/v1/agents",
            new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("type").GetString()
            .Should().Be(ProblemDetailsMapping.PolicyDeniedType);
    }

    [Fact]
    public async Task Client_Throws_AgentControlPlaneException_With_Type_And_Status_On_Error()
    {
        var bad = new AgentManifest(
            "Bad_Id", "nope",
            new AgentHandlerRef("X"),
            Array.Empty<ProtocolBinding>(),
            Array.Empty<ToolRef>());

        await FluentActions.Invoking(async () => await _client.CreateAsync(bad))
            .Should().ThrowAsync<AgentControlPlaneException>()
            .Where(ex => ex.StatusCode == 400 && ex.Type == ProblemDetailsMapping.ManifestInvalidType);
    }

    [Fact]
    public async Task Update_Publishes_New_Version()
    {
        var v1 = new AgentManifest("x", "1.0", new AgentHandlerRef("H"), Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>());
        await _client.CreateAsync(v1);

        var v2 = v1 with { Version = "1.1" };
        var handle = await _client.UpdateAsync("x", v2);
        handle.Version.Should().Be("1.1");

        var list = await _client.ListAsync();
        list.Select(m => m.Version).Should().Contain(new[] { "1.0", "1.1" });
    }

    [Fact]
    public async Task Signal_Returns_202_Accepted()
    {
        var manifest = new AgentManifest("x", "1.0", new AgentHandlerRef("H"), Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>());
        await _client.CreateAsync(manifest);

        // Via raw client to observe the exact status code:
        var body = JsonSerializer.Serialize(new AgentSignal("resume", JsonDocument.Parse("""{"ok":true}""").RootElement));
        using var response = await _http.PostAsync("/v1/agents/x/signal",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Cancel_Mode_Distinguished_From_Evict_Default()
    {
        var manifest = new AgentManifest("x", "1.0", new AgentHandlerRef("H"), Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>());
        await _client.CreateAsync(manifest);

        await _client.CancelAsync("x"); // mode=cancel: handle still resolvable
        (await _client.QueryAsync("x")).Should().NotBeNull();

        await _client.EvictAsync("x"); // mode=evict: gone
        (await _client.QueryAsync("x")).Should().BeNull();
    }

    [Fact]
    public async Task Missing_Required_Field_In_Invoke_Body_Returns_400()
    {
        var manifest = new AgentManifest("x", "1.0", new AgentHandlerRef("H"), Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>());
        await _client.CreateAsync(manifest);

        using var response = await _http.PostAsync("/v1/agents/x/invoke",
            new StringContent("""{"text":""}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class DenyCreatePolicy : IAgentPolicyEngine
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(operation == PolicyOperation.Create
                ? PolicyDecision.Deny("blocked by test policy")
                : PolicyDecision.Allow);
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }
}
