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
using NSubstitute;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// PAM-13: end-to-end integration test that drives the real <c>AgentManifestTranslator</c>
/// through the container-gateway endpoints. Asserts the per-agent <c>LlmGatewayRef</c> /
/// <c>McpGatewayRef</c> chains actually fire on the HTTP boundary — the regression guard
/// for the load-bearing PAM-9..11 fix. Pre-PAM-9 these probes never fire because the
/// handlers used DI-global middleware; post-PAM-9 they fire on every call.
/// </summary>
public sealed class ContainerGatewayPerAgentMiddlewareTests : IAsyncLifetime
{
    private const string TestSecret = "A32CharacterSecretKeyForTestingXX";
    private const string LlmAgentId = "agent-llm";
    private const string ToolAgentId = "agent-tool";
    private const string ProbeServerId = "probe-srv";

    private readonly ProbeLlmMiddleware _llmProbe = new();
    private readonly ProbeToolMiddleware _toolProbe = new();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var llmConfig = new LlmGatewayConfigManifest(
            "rate-limited", "1",
            [new GatewayMiddlewareSpec("Probe")]);
        var mcpConfig = new McpGatewayConfigManifest(
            "tool-governed", "1",
            [new GatewayMiddlewareSpec("Probe")]);

        var llmCfgRegistry = Substitute.For<ILlmGatewayConfigRegistry>();
        llmCfgRegistry.GetAsync("rate-limited", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<LlmGatewayConfigManifest?>(llmConfig));

        var mcpCfgRegistry = Substitute.For<IMcpGatewayConfigRegistry>();
        mcpCfgRegistry.GetAsync("tool-governed", Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpGatewayConfigManifest?>(mcpConfig));

        var llmFactory = Substitute.For<ILlmGatewayMiddlewareFactory>();
        llmFactory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(_llmProbe);

        var toolFactory = Substitute.For<IToolGatewayMiddlewareFactory>();
        toolFactory.Create(Arg.Any<GatewayMiddlewareSpec>()).Returns(_toolProbe);

        var registry = new FakeAgentRegistry();
        registry.Add(new AgentManifest(
            Id: LlmAgentId, Version: "1",
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
            LlmGatewayRef = "rate-limited",
        });
        registry.Add(new AgentManifest(
            Id: ToolAgentId, Version: "1",
            Handler: new AgentHandlerRef("declarative"),
            Protocols: [],
            Tools: [])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
            McpGatewayRef = "tool-governed",
            McpServers = [new McpServerRef(ProbeServerId, McpServerRef.RegisteredTransport)],
        });

        var probeServerManifest = new McpServerManifest(ProbeServerId, "1");
        var serverRegistry = Substitute.For<IMcpServerRegistry>();
        serverRegistry.GetAsync(ProbeServerId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<McpServerManifest?>(probeServerManifest));
        serverRegistry.ListAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(YieldOne(probeServerManifest));

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

                    // Stub pool BEFORE AddAgentManifestInstantiator — its TryAddSingleton
                    // would otherwise install the real pool that needs an IModelProviderFactory.
                    // The probe middleware short-circuits, so the provider is never actually called.
                    services.AddSingleton<ICompletionProviderPool>(new StubProviderPool());

                    // Real IAgentManifestTranslator + supporting registries — the whole point of
                    // this test is to exercise the real per-agent chain resolution end-to-end.
                    services.AddSingleton<IAgentRegistry>(registry);
                    services.AddSingleton(llmCfgRegistry);
                    services.AddSingleton(mcpCfgRegistry);
                    services.AddSingleton(llmFactory);
                    services.AddSingleton(toolFactory);
                    services.AddSingleton<IMcpServerRegistry>(serverRegistry);
                    services.AddSingleton<INamedToolSourceProvider>(
                        new SingleSourceProvider(ProbeServerId, new StubTool("probe-tool")));
                    services.AddAgentManifestInstantiator();

                    services.AddSingleton<AsyncLocalAgentContextAccessor>();
                    services.AddSingleton<IAgentContextAccessor>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());
                    services.AddSingleton<IAgentContextSetter>(sp => sp.GetRequiredService<AsyncLocalAgentContextAccessor>());

                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapContainerGatewayEndpoints());
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
    public async Task LlmComplete_PerAgentLlmGatewayRef_ProbeMiddlewareFires()
    {
        // The agent's manifest carries LlmGatewayRef: "rate-limited" whose config registers a
        // single probe middleware. Pre-PAM-9 this probe never fires (handler used DI-global only).
        var (runId, agentId) = ("run-llm", LlmAgentId);
        var token = MintToken(runId, agentId);

        var body = new
        {
            modelId = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "hi" } },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/llm/complete")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _llmProbe.Invocations.Should().Be(1,
            because: "the agent's LlmGatewayRef-configured middleware must fire on the LLM callback path");
    }

    [Fact]
    public async Task ChatCompletions_PerAgentLlmGatewayRef_ProbeMiddlewareFires()
    {
        // Symmetric coverage for the OpenAI-compat path (PAM-10).
        var (runId, agentId) = ("run-chat", LlmAgentId);
        var token = MintToken(runId, agentId);

        var body = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "hi" } },
            stream = false,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _llmProbe.Invocations.Should().BeGreaterThan(0,
            because: "the agent's LlmGatewayRef-configured middleware must fire on chat/completions too");
    }

    [Fact]
    public async Task ToolInvoke_PerAgentMcpGatewayRef_ProbeMiddlewareFires()
    {
        // The agent's manifest carries McpGatewayRef: "tool-governed" whose config registers a
        // single probe middleware. Pre-PAM-11 this probe never fires (handler used DI-global only).
        var (runId, agentId) = ("run-tool", ToolAgentId);
        var token = MintToken(runId, agentId);

        var body = new
        {
            toolCallId = "call-1",
            toolName = "probe-tool",
            arguments = JsonDocument.Parse("{}").RootElement,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/tools/invoke")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _toolProbe.Invocations.Should().Be(1,
            because: "the agent's McpGatewayRef-configured middleware must fire on the tool callback path");
    }

    [Fact]
    public async Task LlmComplete_UnknownAgent_Returns404()
    {
        var (runId, agentId) = ("run-ghost", "ghost-agent");
        var token = MintToken(runId, agentId);

        var body = new
        {
            modelId = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "hi" } },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/llm/complete")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _llmProbe.Invocations.Should().Be(0, because: "unknown agent must short-circuit before middleware runs");
    }

    [Fact]
    public async Task ToolInvoke_UnknownAgent_Returns404()
    {
        var (runId, agentId) = ("run-ghost", "ghost-agent");
        var token = MintToken(runId, agentId);

        var body = new
        {
            toolCallId = "call-1",
            toolName = "probe-tool",
            arguments = JsonDocument.Parse("{}").RootElement,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/tools/invoke")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _toolProbe.Invocations.Should().Be(0, because: "unknown agent must short-circuit before middleware runs");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string MintToken(string runId, string agentId)
    {
        var svc = _host.Services.GetRequiredService<ICallTokenService>();
        return svc.Generate(runId, agentId, 60);
    }

    private static async IAsyncEnumerable<T> YieldOne<T>(T value)
    {
        await Task.CompletedTask;
        yield return value;
    }

    private sealed class ProbeLlmMiddleware : LlmGatewayMiddleware
    {
        public int Invocations;

        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Invocations);
            // Short-circuit so we don't need a working provider downstream — the assertion that
            // matters is that this middleware was reached at all.
            return Task.FromResult(new CompletionResponse("probe-fired", "fake-model"));
        }

        protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Invocations);
            return ShortCircuitStream();

            static async IAsyncEnumerable<CompletionUpdate> ShortCircuitStream()
            {
                await Task.CompletedTask;
                yield return new CompletionUpdate("probe-fired", ModelId: "fake-model");
            }
        }
    }

    private sealed class ProbeToolMiddleware : ToolGatewayMiddleware
    {
        public int Invocations;

        public override async Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext context,
            Func<Task<ToolCallOutcome>> next,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Invocations);
            return await next();
        }
    }

    private sealed class StubProviderPool : ICompletionProviderPool
    {
        public ValueTask<ICompletionProvider> GetAsync(ModelSpec spec, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ICompletionProvider>(new StubProvider());

        private sealed class StubProvider : ICompletionProvider
        {
            public string ProviderName => "stub";
            public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new CompletionResponse("ok", "stub-model"));
        }
    }

    private sealed class StubTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => "probe";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult("probe-result");
    }

    private sealed class SingleSourceProvider(string name, ITool tool) : INamedToolSourceProvider
    {
        public IToolSource? GetByName(string requestedName)
            => string.Equals(requestedName, name, StringComparison.OrdinalIgnoreCase)
                ? new InlineToolSource(tool)
                : null;

        private sealed class InlineToolSource(ITool inner) : IToolSource
        {
            public async IAsyncEnumerable<ITool> DiscoverAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield return inner;
            }
        }
    }

    private sealed class FakeAgentRegistry : IAgentRegistry
    {
        private readonly List<AgentManifest> _manifests = new();

        public void Add(AgentManifest manifest) => _manifests.Add(manifest);

        public async IAsyncEnumerable<AgentManifest> ListAsync(
            string? labelPrefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var manifest in _manifests) yield return manifest;
        }

        public ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(_manifests.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal)));
    }
}
