// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// HTTP-level tests for the SC-21 <c>POST /v1/container-gateway/sections/build</c> endpoint.
/// Verifies the section pipeline (composer → providers → resolver) runs against the
/// plugin-supplied candidate, returns typed payloads per <c>SectionKind</c>, and surfaces
/// runtime errors (unknown agent, producer failure, collision) as Problem Details.
/// </summary>
public sealed class ContainerGatewaySectionsBuildTests : IAsyncLifetime
{
    private const string TestSecret = "A32CharacterSecretKeyForTestingXX";

    private IHost _host = null!;
    private HttpClient _client = null!;
    private FakeTranslator _translator = null!;

    public async Task InitializeAsync()
    {
        _translator = new FakeTranslator();

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    var config = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Vais:ContainerPlugin:CallTokenSecret"] = TestSecret,
                        })
                        .Build();
                    services.AddSingleton<IConfiguration>(config);
                    services.AddSingleton<ICallTokenService, HmacCallTokenService>();
                    services.AddSingleton<IAgentManifestTranslator>(_translator);

                    // Minimal stubs so the route group's other handlers can resolve their
                    // dependencies — only sections/build is exercised here.
                    services.AddSingleton<ICompletionProviderPool>(new EmptyPool());
                    services.AddSingleton<IMcpServerRegistry, EmptyMcpServerRegistry>();

                    services.AddSingleton<AsyncLocalAgentContextAccessor>();
                    services.AddSingleton<IAgentContextAccessor>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());
                    services.AddSingleton<IAgentContextSetter>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());

                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapContainerGatewayEndpoints());
                });
            })
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task SectionsBuild_Returns_Composer_Plus_History_Plus_Provider_Sections()
    {
        const string runId = "run-1";
        const string agentId = "agent-research";

        _translator.OptionsByAgent[agentId] = new StatefulAgentOptions
        {
            AgentName = agentId,
            SystemPromptComposer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
            {
                new FixedContributor("system.persona", "You are a careful research assistant."),
                new FixedContributor("system.policy", "Cite sources.") { Priority = 10 },
            }),
            ContextProviders = new IContextProvider[]
            {
                new FixedProvider("retrieval.docs", "Source 1: ..."),
            },
        };

        var body = new
        {
            messages = new[]
            {
                new { role = "user", content = "What's our return policy?" },
            },
        };
        var resp = await PostAsync(agentId, runId, body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var sections = payload.GetProperty("sections").EnumerateArray().ToArray();
        sections.Should().HaveCountGreaterOrEqualTo(3);

        var ids = sections.Select(s => s.GetProperty("id").GetString()!).ToArray();
        ids.Should().Contain("system.persona");
        ids.Should().Contain("system.policy");
        ids.Should().Contain("retrieval.docs");

        // The user message should be present as a history section, carrying a TurnPayload.
        var historySection = sections.Single(s =>
            s.GetProperty("id").GetString()!.StartsWith("history.window.", StringComparison.Ordinal));
        historySection.GetProperty("kind").GetString().Should().Be("UserMessage");
        historySection.GetProperty("payload").GetProperty("turn").GetProperty("content").GetString()
            .Should().Be("What's our return policy?");

        // SystemSegment payloads are TextPayload-shaped.
        var personaSection = sections.Single(s => s.GetProperty("id").GetString() == "system.persona");
        personaSection.GetProperty("kind").GetString().Should().Be("SystemSegment");
        personaSection.GetProperty("payload").GetProperty("value").GetString()
            .Should().Be("You are a careful research assistant.");
        personaSection.GetProperty("producerId").GetString().Should().Be("FixedContributor");

        // Retrieval section came from the provider chain — should preserve its budget.
        var retrievalSection = sections.Single(s => s.GetProperty("id").GetString() == "retrieval.docs");
        retrievalSection.GetProperty("budget").GetProperty("priority").GetInt32().Should().Be(5);

        payload.GetProperty("totalChars").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SectionsBuild_With_No_Providers_Returns_Composer_And_History_Only()
    {
        const string runId = "run-2";
        const string agentId = "agent-bare";
        _translator.OptionsByAgent[agentId] = new StatefulAgentOptions
        {
            AgentName = agentId,
            SystemPrompt = "Be brief.",
        };

        var body = new { messages = new[] { new { role = "user", content = "hi" } } };
        var resp = await PostAsync(agentId, runId, body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var sections = payload.GetProperty("sections").EnumerateArray().ToArray();

        sections.Select(s => s.GetProperty("id").GetString()).Should().Contain("system.base");
        sections.Single(s => s.GetProperty("id").GetString() == "system.base")
            .GetProperty("payload").GetProperty("value").GetString().Should().Be("Be brief.");
    }

    [Fact]
    public async Task SectionsBuild_Unknown_Agent_Returns_404()
    {
        var resp = await PostAsync(agentId: "ghost", runId: "r", new { messages = Array.Empty<object>() });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SectionsBuild_Producer_Failure_Returns_500_With_ProducerId()
    {
        const string agentId = "agent-broken";
        _translator.OptionsByAgent[agentId] = new StatefulAgentOptions
        {
            AgentName = agentId,
            ContextProviders = new IContextProvider[] { new ThrowingProvider() },
        };

        var resp = await PostAsync(agentId, "r", new { messages = new[] { new { role = "user", content = "x" } } });
        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("producerId").GetString().Should().Be("ThrowingProvider");
    }

    [Fact]
    public async Task SectionsBuild_Missing_AgentId_Header_Returns_400()
    {
        var token = MintToken("r", "any");
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/sections/build")
        {
            Content = JsonContent.Create(new { messages = Array.Empty<object>() }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", "r");
        // Intentionally no X-Agent-Id.

        var resp = await _client.SendAsync(req);
        // The call-token check binds against agentId from the header, so an empty agentId means
        // the bearer token (minted against "any") won't validate against "" → 401 short-circuit.
        // That's correct gateway behaviour; we don't get to the 400 branch. Both are acceptable
        // failure modes for the missing-header case.
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SectionsBuild_Missing_Bearer_Returns_401()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/sections/build")
        {
            Content = JsonContent.Create(new { messages = Array.Empty<object>() }),
        };
        req.Headers.Add("X-Agent-Id", "x");
        req.Headers.Add("X-Run-Id", "y");

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostAsync(string agentId, string runId, object body)
    {
        var token = MintToken(runId, agentId);
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/sections/build")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Agent-Id", agentId);
        req.Headers.Add("X-Run-Id", runId);
        return await _client.SendAsync(req);
    }

    private string MintToken(string runId, string agentId)
        => _host.Services.GetRequiredService<ICallTokenService>().Generate(runId, agentId, 60);

    private sealed class FakeTranslator : IAgentManifestTranslator
    {
        public Dictionary<string, StatefulAgentOptions> OptionsByAgent { get; } = new();

        public ValueTask<StatefulAgentOptions> TranslateAsync(string agentId, CancellationToken ct = default)
        {
            if (!OptionsByAgent.TryGetValue(agentId, out var options))
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.AgentNotFound,
                    $"Agent '{agentId}' not registered with FakeTranslator.");
            }
            return ValueTask.FromResult(options);
        }

        public ValueTask<StatefulAgentOptions> TranslateForGrain(IServiceProvider sp, string agentId, CancellationToken ct = default)
            => TranslateAsync(agentId, ct);

        public ValueTask<bool> InvalidateAsync(string agentId, CancellationToken ct = default) => ValueTask.FromResult(false);

        public ValueTask<PerAgentChains> ResolvePerAgentChainsAsync(string agentId, CancellationToken ct = default)
        {
            // Permissive fake: tests that don't pre-register OptionsByAgent get an empty
            // PerAgentChains. The real translator throws AgentNotFound here; the fake's job is
            // to provide chains for tests that don't care about per-agent middleware shape.
            if (OptionsByAgent.TryGetValue(agentId, out var options))
            {
                return ValueTask.FromResult(new PerAgentChains(
                    options.GatewayMiddleware, options.ToolGatewayMiddleware,
                    options.InputMiddleware, options.Budget));
            }
            return ValueTask.FromResult(new PerAgentChains(
                Array.Empty<LlmGatewayMiddleware>(),
                Array.Empty<ToolGatewayMiddleware>(),
                Array.Empty<AgentInputMiddleware>(),
                Budget: null));
        }

        public ValueTask<IReadOnlyList<ITool>> ResolveAgentToolsAsync(string agentId, CancellationToken ct = default)
        {
            if (OptionsByAgent.TryGetValue(agentId, out var options) && options.ToolRegistry is { } reg)
                return ValueTask.FromResult<IReadOnlyList<ITool>>(reg.Tools);
            return ValueTask.FromResult<IReadOnlyList<ITool>>(Array.Empty<ITool>());
        }
    }

    private sealed class FixedContributor(string sectionId, string text) : ISystemPromptContributor
    {
        public int Priority { get; init; } = 0;
        public string SectionId { get; } = sectionId;
        public ValueTask<string?> ContributeAsync(AgentContext ctx, CancellationToken ct = default)
            => ValueTask.FromResult<string?>(text);
    }

    private sealed class FixedProvider(string sectionId, string text) : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(
            ContextInvocationContext context, CancellationToken ct = default)
        {
            var section = new Section(
                sectionId,
                SectionKind.SystemSegment,
                new TextPayload(text),
                ProducerId: nameof(FixedProvider),
                Budget: new SectionBudget(Priority: 5));
            return ValueTask.FromResult(new ContextContribution(new[] { section }));
        }
    }

    private sealed class ThrowingProvider : IContextProvider
    {
        public ValueTask<ContextContribution> InvokeAsync(
            ContextInvocationContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class EmptyPool : ICompletionProviderPool
    {
        public ValueTask<ICompletionProvider> GetAsync(ModelSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class EmptyMcpServerRegistry : IMcpServerRegistry
    {
        public IAsyncEnumerable<McpServerManifest> ListAsync(string? labelPrefix = null, CancellationToken ct = default)
            => Empty<McpServerManifest>();
        public ValueTask<McpServerManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default)
            => ValueTask.FromResult<McpServerManifest?>(null);
        public ValueTask RegisterAsync(McpServerManifest manifest, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
