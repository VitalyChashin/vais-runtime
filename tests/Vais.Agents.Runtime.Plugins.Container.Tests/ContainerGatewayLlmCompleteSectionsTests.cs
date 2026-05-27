// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
/// HTTP-level tests for the contract v0.27 extension to <c>POST /v1/container-gateway/llm/complete</c>:
/// the body becomes a discriminated union of <c>{ messages }</c> (legacy, unchanged) and
/// <c>{ sections }</c> (new — runtime runs the section pipeline server-side, restoring per-section
/// telemetry symmetry with runtime-hosted agents). Covers the discriminator rules, the
/// resolver→packer→telemetry→flatten pipeline, the streaming form, and the canonical-vs-legacy
/// coexistence story.
/// </summary>
public sealed class ContainerGatewayLlmCompleteSectionsTests : IAsyncLifetime
{
    private const string TestSecret = "A32CharacterSecretKeyForTestingXX";

    private IHost _host = null!;
    private HttpClient _client = null!;
    private FakeTranslator _translator = null!;
    private FakeProvider _provider = null!;
    private RecordingTelemetrySink _sink = null!;

    public async Task InitializeAsync()
    {
        _translator = new FakeTranslator();
        _provider = new FakeProvider();
        _sink = new RecordingTelemetrySink();

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
                    services.AddSingleton<ICompletionProviderPool>(new FakeProviderPool(_provider));
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

    // ── Backwards compatibility — messages variant unchanged ─────────────────

    [Fact]
    public async Task Messages_Variant_Returns_Completion_Without_Translator_Or_Pipeline()
    {
        // The messages variant must keep working without invoking the section pipeline at all —
        // that's the entire backwards-compatibility contract for v0.27.
        var body = new
        {
            modelId = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "hi" } },
        };
        var resp = await PostAsync(body, agentId: "agent-1", runId: "run-1");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("message").GetProperty("content").GetString().Should().Be("fake-reply");

        // Telemetry sink must NOT fire on the messages path — there are no sections.
        _sink.Snapshots.Should().BeEmpty();
    }

    // ── Sections variant — pipeline runs server-side ─────────────────────────

    [Fact]
    public async Task Sections_Variant_Runs_Pipeline_And_Returns_Completion()
    {
        const string agentId = "agent-sectioned";
        _translator.OptionsByAgent[agentId] = new StatefulAgentOptions
        {
            AgentName = agentId,
            SectionTelemetrySinks = new[] { (ISectionTelemetrySink)_sink },
        };

        var body = new
        {
            modelId = "gpt-4o-mini",
            sections = new object[]
            {
                new
                {
                    id = "system.persona",
                    kind = "SystemSegment",
                    payload = new { value = "You are a careful research assistant." },
                    producerId = "PersonaContributor",
                },
                new
                {
                    id = "history.window.0",
                    kind = "UserMessage",
                    payload = new { turn = new { role = "user", content = "What's our return policy?" } },
                    producerId = "Base",
                },
            },
        };

        var resp = await PostAsync(body, agentId: agentId, runId: "run-7");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("message").GetProperty("content").GetString().Should().Be("fake-reply");

        // The provider should have received a CompletionRequest with the SystemSegment flattened
        // into SystemPrompt and the turn-shaped section in History — that's the canonical flatten
        // the runtime performs server-side instead of letting the plugin do it.
        _provider.LastRequest.Should().NotBeNull();
        _provider.LastRequest!.SystemPrompt.Should().Be("You are a careful research assistant.");
        _provider.LastRequest.History.Should().HaveCount(1);
        _provider.LastRequest.History[0].Text.Should().Be("What's our return policy?");

        // Telemetry symmetry — the emitter must fire on the sections path. This is the regression
        // the v0.27 contract bump fixed.
        _sink.Snapshots.Should().HaveCount(1);
        _sink.Snapshots[0].Sections.Should().HaveCount(2);
        _sink.Snapshots[0].Context.AgentName.Should().Be(agentId);
        _sink.Snapshots[0].Context.RunId.Should().Be("run-7");
    }

    // ── Discriminator rules ──────────────────────────────────────────────────

    [Fact]
    public async Task Both_Messages_And_Sections_Populated_Returns_400()
    {
        var body = new
        {
            messages = new[] { new { role = "user", content = "x" } },
            sections = new[] { new { id = "system.persona", kind = "SystemSegment", payload = new { value = "p" } } },
        };
        var resp = await PostAsync(body, agentId: "any", runId: "any");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("urn").GetString().Should().Be("urn:vais-agents:llm-complete-input-conflict");
    }

    [Fact]
    public async Task Neither_Messages_Nor_Sections_Populated_Returns_400()
    {
        var resp = await PostAsync(new { modelId = "gpt-4o-mini" }, agentId: "any", runId: "any");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("urn").GetString().Should().Be("urn:vais-agents:llm-complete-input-conflict");
    }

    // ── Failure modes for the sections variant ───────────────────────────────

    [Fact]
    public async Task Sections_Variant_Unknown_Agent_Returns_404()
    {
        var body = new
        {
            sections = new[] { new { id = "system.persona", kind = "SystemSegment", payload = new { value = "p" } } },
        };
        var resp = await PostAsync(body, agentId: "ghost", runId: "r");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sections_Variant_Section_Collision_Returns_400()
    {
        const string agentId = "agent-collide";
        _translator.OptionsByAgent[agentId] = new StatefulAgentOptions { AgentName = agentId };

        var body = new
        {
            sections = new[]
            {
                new { id = "dup.id", kind = "SystemSegment", payload = new { value = "a" } },
                new { id = "dup.id", kind = "SystemSegment", payload = new { value = "b" } },
            },
        };
        var resp = await PostAsync(body, agentId: agentId, runId: "r");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Streaming form ───────────────────────────────────────────────────────

    [Fact]
    public async Task Sections_Variant_Streaming_Emits_VaisNative_Sse_Frames()
    {
        const string agentId = "agent-stream";
        _translator.OptionsByAgent[agentId] = new StatefulAgentOptions { AgentName = agentId };

        _provider.StreamingChunks = new[] { "alpha ", "beta ", "gamma" };

        var body = new
        {
            sections = new object[]
            {
                new { id = "system.persona", kind = "SystemSegment", payload = new { value = "be brief" } },
                new
                {
                    id = "history.window.0", kind = "UserMessage",
                    payload = new { turn = new { role = "user", content = "go" } },
                },
            },
        };

        var token = MintToken("run-s", agentId);
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/llm/complete")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", "run-s");
        req.Headers.Add("X-Agent-Id", agentId);
        req.Headers.Add("Accept", "text/event-stream");

        var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        var text = await resp.Content.ReadAsStringAsync();

        // One delta frame per chunk + a terminal done frame.
        text.Should().Contain("event: delta");
        text.Should().Contain("\"textDelta\":\"alpha \"");
        text.Should().Contain("\"textDelta\":\"beta \"");
        text.Should().Contain("\"textDelta\":\"gamma\"");
        text.Should().Contain("event: done");

        // Wire shape must be VAIS-native — never OpenAI's chat.completion.chunk shape on this route.
        text.Should().NotContain("chat.completion.chunk");
        text.Should().NotContain("data: [DONE]");
    }

    [Fact]
    public async Task Messages_Variant_Streaming_Also_Works_For_Symmetry()
    {
        _provider.StreamingChunks = new[] { "x ", "y" };

        var body = new
        {
            messages = new[] { new { role = "user", content = "hi" } },
        };

        var token = MintToken("run-m", "agent-m");
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/llm/complete")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", "run-m");
        req.Headers.Add("X-Agent-Id", "agent-m");
        req.Headers.Add("Accept", "text/event-stream");

        var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var text = await resp.Content.ReadAsStringAsync();
        text.Should().Contain("\"textDelta\":\"x \"");
        text.Should().Contain("\"textDelta\":\"y\"");
        text.Should().Contain("event: done");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostAsync(object body, string agentId, string runId)
    {
        var token = MintToken(runId, agentId);
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/llm/complete")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);
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
                    $"Agent '{agentId}' not registered.");
            }
            return ValueTask.FromResult(options);
        }

        public ValueTask<StatefulAgentOptions> TranslateForGrain(IServiceProvider sp, string agentId, CancellationToken ct = default)
            => TranslateAsync(agentId, ct);

        public ValueTask<bool> InvalidateAsync(string agentId, CancellationToken ct = default)
            => ValueTask.FromResult(false);

        public ValueTask<PerAgentChains> ResolvePerAgentChainsAsync(string agentId, CancellationToken ct = default)
            => throw new NotImplementedException("FakeTranslator does not implement ResolvePerAgentChainsAsync.");
    }

    private sealed class FakeProviderPool(FakeProvider provider) : ICompletionProviderPool
    {
        public ValueTask<ICompletionProvider> GetAsync(ModelSpec spec, CancellationToken ct = default)
            => ValueTask.FromResult<ICompletionProvider>(provider);
    }

    private sealed class FakeProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        public string ProviderName => "fake";
        public CompletionRequest? LastRequest { get; private set; }
        public IReadOnlyList<string> StreamingChunks { get; set; } = ["ok"];

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new CompletionResponse("fake-reply", "fake-model", PromptTokens: 1, CompletionTokens: 1));
        }

        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
            CompletionRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            foreach (var chunk in StreamingChunks)
            {
                yield return new CompletionUpdate(chunk, ModelId: "fake-model");
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingTelemetrySink : ISectionTelemetrySink
    {
        public List<SectionTelemetrySnapshot> Snapshots { get; } = new();

        public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken ct = default)
        {
            Snapshots.Add(snapshot);
            return ValueTask.CompletedTask;
        }
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
