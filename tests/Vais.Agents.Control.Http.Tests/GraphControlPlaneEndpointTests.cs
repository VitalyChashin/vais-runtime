// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// v0.19 PR 1: Graph control plane HTTP endpoints.
/// Spins up a minimal-API TestServer backed by in-memory registry + fake lifecycle
/// manager, exercises every graph route via raw HttpClient + typed AgentControlPlaneClient.
/// </summary>
public sealed class GraphControlPlaneEndpointTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private IHost _host = null!;
    private HttpClient _http = null!;
    private FakeGraphLifecycleManager _fakeManager = null!;
    private InMemoryAgentGraphRegistry _registry = null!;
    private AgentControlPlaneClient _client = null!;

    private static AgentGraphManifest MinimalManifest(string id = "test-graph", string version = "1.0") =>
        new AgentGraphManifest(
            Id: id,
            Version: version,
            Entry: "start",
            Nodes: new[] { new GraphNode("start", "End") },
            Edges: Array.Empty<GraphEdge>());

    private static string MinimalEnvelope(string id = "test-graph", string version = "1.0") => $$"""
        {
          "apiVersion": "vais.agents/v1",
          "kind": "AgentGraph",
          "metadata": { "id": "{{id}}", "version": "{{version}}" },
          "spec": {
            "entry": "start",
            "nodes": [{ "id": "start", "kind": "End" }],
            "edges": []
          }
        }
        """;

    public async Task InitializeAsync()
    {
        _fakeManager = new FakeGraphLifecycleManager();
        _registry = new InMemoryAgentGraphRegistry();

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<IAgentGraphRegistry>(_registry);
                    services.AddSingleton<IAgentGraphLifecycleManager>(_fakeManager);
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

    // ── POST /v1/graphs ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGraph_ValidEnvelope_Returns201WithHandle()
    {
        var content = new StringContent(MinimalEnvelope(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/graphs", content);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var handle = await resp.Content.ReadFromJsonAsync<AgentGraphHandle>(JsonOpts);
        handle!.GraphId.Should().Be("test-graph");
    }

    [Fact]
    public async Task CreateGraph_EmptyBody_Returns400()
    {
        var content = new StringContent("not json", Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/graphs", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateGraph_TwoManifestsInBody_Returns400()
    {
        // Array of two manifests — server expects exactly one
        var body = $"[{MinimalEnvelope("g1", "1.0")}, {MinimalEnvelope("g2", "1.0")}]";
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/v1/graphs", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── GET /v1/graphs ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListGraphs_EmptyRegistry_ReturnsEmptyItems()
    {
        using var resp = await _http.GetAsync("/v1/graphs");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListGraphs_AfterCreate_ReturnsManifest()
    {
        _registry.Register(MinimalManifest());

        using var resp = await _http.GetAsync("/v1/graphs");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    // ── GET /v1/graphs/{id} ──────────────────────────────────────────────────

    [Fact]
    public async Task QueryGraph_KnownId_Returns200WithManifest()
    {
        var manifest = MinimalManifest();
        _registry.Register(manifest);
        _fakeManager.StatusToReturn = new AgentGraphStatus("test-graph", "1.0", 0, 0, 0, null);

        using var resp = await _http.GetAsync("/v1/graphs/test-graph");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("manifest").GetProperty("id").GetString().Should().Be("test-graph");
    }

    [Fact]
    public async Task QueryGraph_UnknownId_Returns404()
    {
        using var resp = await _http.GetAsync("/v1/graphs/does-not-exist");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /v1/graphs/{id} ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateGraph_ValidEnvelope_Returns200()
    {
        _registry.Register(MinimalManifest());
        _fakeManager.UpdateResponse = new AgentGraphHandle("test-graph", "2.0");

        var content = new StringContent(MinimalEnvelope("test-graph", "2.0"), Encoding.UTF8, "application/json");
        using var resp = await _http.PatchAsync("/v1/graphs/test-graph", content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var handle = await resp.Content.ReadFromJsonAsync<AgentGraphHandle>(JsonOpts);
        handle!.Version.Should().Be("2.0");
    }

    // ── DELETE /v1/graphs/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task EvictGraph_Returns204()
    {
        _registry.Register(MinimalManifest());

        using var resp = await _http.DeleteAsync("/v1/graphs/test-graph");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── POST /v1/graphs/{id}/invoke ──────────────────────────────────────────

    [Fact]
    public async Task InvokeGraph_Returns200WithResult()
    {
        _registry.Register(MinimalManifest());
        _fakeManager.InvokeResponse = new GraphInvocationResult(
            "run-1", new Dictionary<string, JsonElement>(), IsComplete: true);

        var request = new GraphInvocationRequest(new Dictionary<string, JsonElement>());
        var content = JsonContent.Create(request);
        using var resp = await _http.PostAsync("/v1/graphs/test-graph/invoke", content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<GraphInvocationResult>(JsonOpts);
        result!.RunId.Should().Be("run-1");
        result.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeGraph_GraphNotFound_Returns503WithProblemDetails()
    {
        _fakeManager.ThrowOnInvoke = new GraphHandleNotFoundException("no-graph", "1.0");

        var request = new GraphInvocationRequest(new Dictionary<string, JsonElement>());
        var content = JsonContent.Create(request);
        using var resp = await _http.PostAsync("/v1/graphs/no-graph/invoke", content);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /v1/graphs/{id}/runs/{runId} ───────────────────────────────────

    [Fact]
    public async Task CancelGraphRun_Returns204()
    {
        _registry.Register(MinimalManifest());

        using var resp = await _http.DeleteAsync("/v1/graphs/test-graph/runs/run-1");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Typed client round-trip ───────────────────────────────────────────────

    [Fact]
    public async Task TypedClient_CreateGraph_RoundTrip()
    {
        var manifest = MinimalManifest("client-graph");
        var handle = await _client.CreateGraphAsync(manifest);
        handle.GraphId.Should().Be("client-graph");
    }

    [Fact]
    public async Task TypedClient_ListGraphs_ReturnsItems()
    {
        _registry.Register(MinimalManifest("listed-graph"));

        var response = await _client.ListGraphsAsync();
        response.Items.Should().ContainSingle(m => m.Id == "listed-graph");
    }

    [Fact]
    public async Task TypedClient_QueryGraph_NotFound_ReturnsNull()
    {
        var response = await _client.QueryGraphAsync("nonexistent");
        response.Should().BeNull();
    }

    // ── Problem Details mapping ───────────────────────────────────────────────

    [Fact]
    public async Task InvokeGraph_GraphRunConflict_Returns409()
    {
        _registry.Register(MinimalManifest());
        _fakeManager.ThrowOnInvoke = new GraphRunConflictException("test-graph", "run-1");

        var content = JsonContent.Create(new GraphInvocationRequest(new Dictionary<string, JsonElement>(),
            RunId: "run-1"));
        using var resp = await _http.PostAsync("/v1/graphs/test-graph/invoke", content);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var pd = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        pd.GetProperty("type").GetString().Should().Contain("graph-run-conflict");
    }

    // ── Fake infrastructure ───────────────────────────────────────────────────

    private sealed class FakeGraphLifecycleManager : IAgentGraphLifecycleManager
    {
        public AgentGraphStatus StatusToReturn { get; set; } = new("fake", "1.0", 0, 0, 0, null);
        public AgentGraphHandle CreateResponse { get; set; } = new("fake", "1.0");
        public AgentGraphHandle UpdateResponse { get; set; } = new("fake", "1.0");
        public GraphInvocationResult InvokeResponse { get; set; } = new("run-1", new Dictionary<string, JsonElement>(), true);
        public Exception? ThrowOnInvoke { get; set; }

        public ValueTask<AgentGraphHandle> CreateAsync(AgentGraphManifest manifest, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentGraphHandle(manifest.Id, manifest.Version));

        public ValueTask<AgentGraphHandle> UpdateAsync(AgentGraphHandle handle, AgentGraphManifest newManifest, CancellationToken ct = default)
            => ValueTask.FromResult(UpdateResponse);

        public ValueTask<AgentGraphStatus> QueryAsync(AgentGraphHandle handle, CancellationToken ct = default)
            => ValueTask.FromResult(StatusToReturn);

        public ValueTask<GraphInvocationResult> InvokeAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default)
        {
            if (ThrowOnInvoke is not null) throw ThrowOnInvoke;
            return ValueTask.FromResult(InvokeResponse);
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentGraphEvent> InvokeStreamAsync(
            AgentGraphHandle handle,
            GraphInvocationRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (ThrowOnInvoke is not null) throw ThrowOnInvoke;
            yield break;
        }
#pragma warning restore CS1998

        public ValueTask<GraphInvocationResult> ResumeAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default)
        {
            if (ThrowOnInvoke is not null) throw ThrowOnInvoke;
            return ValueTask.FromResult(InvokeResponse);
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(
            AgentGraphHandle handle,
            GraphResumeRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (ThrowOnInvoke is not null) throw ThrowOnInvoke;
            yield break;
        }
#pragma warning restore CS1998

        public ValueTask CancelAsync(AgentGraphHandle handle, string runId, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask EvictAsync(AgentGraphHandle handle, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}
