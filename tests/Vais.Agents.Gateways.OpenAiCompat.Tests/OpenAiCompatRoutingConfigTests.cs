// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Options;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Gateways.OpenAiCompat.Tests;

/// <summary>
/// RC-1 through RC-6 — config-driven routing flag tests.
/// </summary>
public sealed class OpenAiCompatRoutingConfigTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task<IHost> BuildHostAsync(
        Action<IServiceCollection> configureServices,
        Action<OpenAiCompatOptions>? configureOptions = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddOpenAiCompatGateway(configureOptions);
                    services.AddPassThroughIdentityResolver();
                    services.AddInMemoryModelRouter(_ => { });
                    configureServices(services);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapOpenAiCompat());
                });
            })
            .StartAsync();
    }

    // ── RC-3: ModelsEndpoint honours routing flags ────────────────────────────

    [Fact]
    public async Task ModelsEndpoint_AgentRoutingDisabled_ExcludesAgentModels()
    {
        var host = await BuildHostAsync(
            services =>
            {
                services.AddSingleton<IAgentRegistry>(
                    new FakeRegistryForConfig([MakeAgentManifest("foo")]));
                services.AddSingleton<IAgentGraphRegistry>(
                    new FakeGraphRegistryForConfig([MakeGraphManifest("g1", annotated: true)]));
            },
            o => o.AgentRoutingEnabled = false);

        using var http = host.GetTestClient();
        try
        {
            var response = await http.GetAsync("/v1/models");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var ids = json.GetProperty("data").EnumerateArray()
                .Select(el => el.GetProperty("id").GetString())
                .ToArray();

            ids.Should().NotContain("agent:foo");
            ids.Should().Contain("graph:g1");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ModelsEndpoint_GraphRoutingDisabled_ExcludesGraphModels()
    {
        var host = await BuildHostAsync(
            services =>
            {
                services.AddSingleton<IAgentRegistry>(
                    new FakeRegistryForConfig([MakeAgentManifest("foo")]));
                services.AddSingleton<IAgentGraphRegistry>(
                    new FakeGraphRegistryForConfig([MakeGraphManifest("g1", annotated: true)]));
            },
            o => o.GraphRoutingEnabled = false);

        using var http = host.GetTestClient();
        try
        {
            var response = await http.GetAsync("/v1/models");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var ids = json.GetProperty("data").EnumerateArray()
                .Select(el => el.GetProperty("id").GetString())
                .ToArray();

            ids.Should().Contain("agent:foo");
            ids.Should().NotContain("graph:g1");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── RC-4: ChatCompletion honours routing flags ────────────────────────────

    [Fact]
    public async Task ChatCompletion_AgentRoutingDisabled_Returns404WithMessage()
    {
        var host = await BuildHostAsync(
            _ => { },
            o => o.AgentRoutingEnabled = false);

        using var http = host.GetTestClient();
        try
        {
            var body = new { model = "agent:foo", messages = new[] { new { role = "user", content = "hi" } } };
            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var text = await response.Content.ReadAsStringAsync();
            text.Should().Contain("agent routing is disabled");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Fact]
    public async Task ChatCompletion_GraphRoutingDisabled_Returns404WithMessage()
    {
        var host = await BuildHostAsync(
            _ => { },
            o => o.GraphRoutingEnabled = false);

        using var http = host.GetTestClient();
        try
        {
            var body = new { model = "graph:foo", messages = new[] { new { role = "user", content = "hi" } } };
            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var text = await response.Content.ReadAsStringAsync();
            text.Should().Contain("graph routing is disabled");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── RC-2: AddOpenAiCompatGateway option binding ───────────────────────────

    [Fact]
    public void AddOpenAiCompatGateway_NoArgs_DefaultsBothEnabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddOpenAiCompatGateway();

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<OpenAiCompatOptions>>().Value;

        options.AgentRoutingEnabled.Should().BeTrue();
        options.GraphRoutingEnabled.Should().BeTrue();
    }

    [Fact]
    public void AddOpenAiCompatGateway_CodeOverride_DisablesPath()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddOpenAiCompatGateway(o => o.AgentRoutingEnabled = false);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<OpenAiCompatOptions>>().Value;

        options.AgentRoutingEnabled.Should().BeFalse();
        options.GraphRoutingEnabled.Should().BeTrue();
    }

    // ── Fixture helpers ───────────────────────────────────────────────────────

    private static AgentManifest MakeAgentManifest(string id) => new(
        Id: id,
        Version: "1.0.0",
        Handler: new AgentHandlerRef("FakeAgent"),
        Protocols: [],
        Tools: []);

    private static AgentGraphManifest MakeGraphManifest(string id, bool annotated) => new(
        Id: id,
        Version: "1.0.0",
        Entry: "start",
        Nodes: [new GraphNode("start", "End")],
        Edges: [],
        Annotations: annotated
            ? new Dictionary<string, string>
            {
                ["vais.io/openai-compat-input-key"] = "messages",
                ["vais.io/openai-compat-output-key"] = "output"
            }
            : null);

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeRegistryForConfig(IReadOnlyList<AgentManifest> manifests) : IAgentRegistry
    {
        public async IAsyncEnumerable<AgentManifest> ListAsync(
            string? labelPrefix = null,
            [global::System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var m in manifests)
                yield return m;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask<AgentManifest?> GetAsync(string id, string? version, CancellationToken cancellationToken)
            => ValueTask.FromResult(manifests.FirstOrDefault(m => m.Id == id));

        public Task PutAsync(AgentManifest manifest, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string id, string? version, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeGraphRegistryForConfig(IReadOnlyList<AgentGraphManifest> manifests) : IAgentGraphRegistry
    {
        public async IAsyncEnumerable<AgentGraphManifest> ListAsync(
            string? labelPrefix = null,
            [global::System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var m in manifests)
                yield return m;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask<AgentGraphManifest?> GetAsync(string id, string? version, CancellationToken cancellationToken)
            => ValueTask.FromResult(manifests.FirstOrDefault(m => m.Id == id));

        public Task PutAsync(AgentGraphManifest manifest, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(string id, string? version, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
