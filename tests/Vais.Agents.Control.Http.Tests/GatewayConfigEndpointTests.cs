// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
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
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// GCF-19 Phase 2, scenarios 5-7: HTTP endpoint smoke tests + gateway ref validation
/// for the LLM gateway config control-plane routes.
/// </summary>
public sealed class GatewayConfigEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private IHost _host = null!;
    private HttpClient _http = null!;
    private InMemoryLlmGatewayConfigRegistry _llmRegistry = null!;
    private InMemoryMcpServerRegistry _serverRegistry = null!;

    private static string LlmGatewayEnvelope(string id = "test-llm-gw", string version = "1.0") => $$"""
        {
          "apiVersion": "vais.agents/v1",
          "kind": "LlmGatewayConfig",
          "metadata": { "id": "{{id}}", "version": "{{version}}" },
          "spec": { "middleware": [] }
        }
        """;

    private static string AgentEnvelope(string id, string? llmGatewayRef = null, string? mcpServerName = null) =>
        $$"""
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "{{id}}", "version": "1.0" },
          "spec": {
            "handler": { "typeName": "Vais.Test.FakeHandler" },
            "protocols": [],
            "tools": []
            {{(llmGatewayRef is not null ? $", \"llmGatewayRef\": \"{llmGatewayRef}\"" : "")}}
            {{(mcpServerName is not null ? $", \"mcpServers\": [{{\"name\": \"{mcpServerName}\", \"transport\": \"registered\"}}]" : "")}}
          }
        }
        """;

    public async Task InitializeAsync()
    {
        _llmRegistry = new InMemoryLlmGatewayConfigRegistry();
        _serverRegistry = new InMemoryMcpServerRegistry();

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ILlmGatewayConfigRegistry>(_llmRegistry);
                    services.AddSingleton<ILlmGatewayConfigLifecycleManager>(
                        new LlmGatewayConfigLifecycleManager(_llmRegistry));
                    services.AddSingleton<IMcpServerRegistry>(_serverRegistry);
                    services.AddSingleton<IAgentRegistry>(new InMemoryAgentRegistry());
                    services.AddSingleton<IAgentLifecycleManager>(new FakeAgentLifecycleManager());
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapAgentControlPlane());
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

    // ── Scenario 5: LLM gateway HTTP smoke tests ─────────────────────────────

    [Fact]
    public async Task CreateLlmGateway_ValidEnvelope_Returns201WithHandle()
    {
        var content = new StringContent(LlmGatewayEnvelope(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/llm-gateways", content);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("handle").GetProperty("id").GetString().Should().Be("test-llm-gw");
    }

    [Fact]
    public async Task QueryLlmGateway_AfterCreate_Returns200()
    {
        var content = new StringContent(LlmGatewayEnvelope(), Encoding.UTF8, "application/json");
        await _http.PostAsync("/v1/llm-gateways", content);

        using var resp = await _http.GetAsync("/v1/llm-gateways/test-llm-gw");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("manifest").GetProperty("id").GetString().Should().Be("test-llm-gw");
    }

    [Fact]
    public async Task EvictLlmGateway_AfterCreate_Returns204()
    {
        var content = new StringContent(LlmGatewayEnvelope(), Encoding.UTF8, "application/json");
        await _http.PostAsync("/v1/llm-gateways", content);

        using var resp = await _http.DeleteAsync("/v1/llm-gateways/test-llm-gw");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ValidateLlmGateway_ValidEnvelope_Returns200ValidTrue()
    {
        var content = new StringContent(LlmGatewayEnvelope(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/llm-gateways/validate", content);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("valid").GetBoolean().Should().BeTrue();
    }

    // ── Scenario 6: LlmGatewayRef validation ─────────────────────────────────

    [Fact]
    public async Task CreateAgent_WithRegisteredLlmGatewayRef_Returns201()
    {
        await _llmRegistry.RegisterAsync(
            new LlmGatewayConfigManifest("gw-exists", "1.0", Array.Empty<GatewayMiddlewareSpec>()));

        var content = new StringContent(AgentEnvelope("agent-a", llmGatewayRef: "gw-exists"), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/agents", content);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateAgent_WithMissingLlmGatewayRef_Returns422()
    {
        var content = new StringContent(AgentEnvelope("agent-b", llmGatewayRef: "gw-missing"), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/agents", content);

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ── Scenario 7: McpServer registered-transport ref validation ────────────

    [Fact]
    public async Task CreateAgent_WithRegisteredMcpServerTransport_UnknownServerId_Returns422()
    {
        var content = new StringContent(AgentEnvelope("agent-c", mcpServerName: "srv-missing"), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/agents", content);

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ── Fake infrastructure ───────────────────────────────────────────────────

    private sealed class FakeAgentLifecycleManager : IAgentLifecycleManager
    {
        public ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentHandle(manifest.Id, manifest.Version));
        public ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentInvocationResult(string.Empty));
        public ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentStatus.Idle);
        public ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentHandle(newManifest.Id, newManifest.Version));
        public ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
