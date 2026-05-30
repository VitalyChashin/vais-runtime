// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
/// Part 4 (south-coverage) SC-T1 / SC-T3 regression tests for D-15.
/// Confirms that the container-gateway <c>POST /v1/container-gateway/tools/invoke</c>
/// endpoint emits <see cref="ToolCallStarted"/>/<see cref="ToolCallCompleted"/> on the
/// in-memory event bus, keyed to the caller's RunId — proving plugin tool calls are
/// observable on the same path as in-process tool calls (no D-15 bypass).
/// </summary>
public sealed class ContainerGatewayToolInvokeObservabilityTests : IAsyncLifetime
{
    private const string TestSecret = "A32CharacterSecretKeyForTestingXY";

    private IHost _host = null!;
    private HttpClient _client = null!;
    private CapturingEventBus _bus = null!;

    public async Task InitializeAsync()
    {
        _bus = new CapturingEventBus();

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
                    services.AddSingleton(new ContainerPluginLoaderOptions { RenewTokenTtlSeconds = 90 });
                    services.AddSingleton<IInvokeLeaseStore, InMemoryInvokeLeaseStore>();
                    services.AddSingleton(sp => new LeaseLivenessCache(sp.GetRequiredService<IInvokeLeaseStore>()));

                    services.AddSingleton<IAgentManifestTranslator, PermissiveFakeTranslator>();

                    // Required for route matcher init: ContainerGatewayEndpoints maps both LLM and
                    // tool routes from a single group; if any handler in the group can't resolve its
                    // parameters, the matcher init throws on the first request to ANY route. We only
                    // exercise tools/invoke, but ICompletionProviderPool must be present so the LLM
                    // handlers' parameter inference succeeds.
                    services.AddSingleton<ICompletionProviderPool>(new NoopCompletionProviderPool());

                    // Inject the in-memory bus so DefaultToolCallDispatcher emits ToolCallStarted/Completed.
                    services.AddSingleton<IAgentEventBus>(_bus);

                    // Register an MCP server whose tools are exposed by the fake provider below.
                    services.AddSingleton<IMcpServerRegistry>(new SingleServerRegistry(
                        new McpServerManifest("test-mcp", "1.0")));

                    // Provider that maps "test-mcp" → tool source exposing echo + fail tools.
                    services.AddSingleton<INamedToolSourceProvider>(new FakeToolSourceProvider(
                        "test-mcp",
                        new EchoTool("echo-tool"),
                        new ErrorTool("fail-tool")));

                    // The tools/invoke handler takes IEnumerable<IToolGuardrail> from DI. Minimal-API
                    // parameter binding needs at least one registration so IServiceProviderIsService
                    // recognises the type as a service (else the binder treats it as a body parameter
                    // — and since the request body is already bound to GatewayToolInvokeRequest, it
                    // throws "Failure to infer one or more parameters").
                    services.AddSingleton<IToolGuardrail, NoopGuardrail>();

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

    // ── SC-T1: successful tool call emits ToolCallCompleted keyed to RunId ────

    [Fact]
    public async Task ToolInvoke_Success_EmitsToolCallCompleted_KeyedToRunId()
    {
        var (runId, agentId, callId) = ($"run-t1-{Guid.NewGuid():N}", "agent-t1", "call-1");
        _bus.Events.Clear();

        var resp = await PostToolInvoke("echo-tool", callId, runId, agentId);
        resp.IsSuccessStatusCode.Should().BeTrue($"endpoint must accept the request; got {resp.StatusCode}");

        var completed = _bus.Events.OfType<ToolCallCompleted>()
            .Where(e => e.Context.RunId == runId)
            .Should().ContainSingle().Subject;
        completed.ToolName.Should().Be("echo-tool");
        completed.CallId.Should().Be(callId);
        completed.Succeeded.Should().BeTrue();
        completed.Level.Should().Be(FailureLevel.Default, "a successful call is clean");
        completed.Context.RunId.Should().Be(runId,
            "RunId from X-Run-Id header must reach the event — closes the D-15 attribution path");
    }

    // ── SC-T1 (error branch): failed tool call emits ToolCallCompleted{Level=Warning} ──

    [Fact]
    public async Task ToolInvoke_ToolThrows_EmitsToolCallCompleted_LevelWarning_KeyedToRunId()
    {
        var (runId, agentId, callId) = ($"run-t1err-{Guid.NewGuid():N}", "agent-t1err", "call-err");
        _bus.Events.Clear();

        var resp = await PostToolInvoke("fail-tool", callId, runId, agentId);
        // Recovered failure: gateway returns 200, the failure is surfaced via the event.
        resp.IsSuccessStatusCode.Should().BeTrue();

        var completed = _bus.Events.OfType<ToolCallCompleted>()
            .Where(e => e.Context.RunId == runId)
            .Should().ContainSingle().Subject;
        completed.ToolName.Should().Be("fail-tool");
        completed.Succeeded.Should().BeFalse();
        completed.Level.Should().Be(FailureLevel.Warning,
            "a tool error fed back to the caller is a recovered failure — WARNING, not ERROR");
        completed.Context.RunId.Should().Be(runId);
    }

    // ── SC-T3: tool invoke without a bearer token is rejected ──────────────────
    // Structural enforcement that plugins cannot call tools off the observed path:
    // the gateway endpoint requires a valid call-token, and the Python SDK only
    // exposes gateway-routed tool surfaces (see audit SC-A2).

    [Fact]
    public async Task ToolInvoke_NoBearerToken_Returns401()
    {
        var body = new { toolName = "echo-tool", toolCallId = "c-noauth", arguments = new { } };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/tools/invoke")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("X-Run-Id", "run-noauth");
        req.Headers.Add("X-Agent-Id", "agent-noauth");

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized,
            "the gateway rejects tool calls that lack a valid call-token bearer");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostToolInvoke(
        string toolName, string callId, string runId, string agentId)
    {
        var token = _host.Services.GetRequiredService<ICallTokenService>().Generate(runId, agentId, 60);
        var body = new
        {
            toolName,
            toolCallId = callId,
            arguments  = new { },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/tools/invoke")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);
        return await _client.SendAsync(req);
    }

    // ── test doubles ──────────────────────────────────────────────────────────

    private sealed class EchoTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "echo";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult("echo-result");
    }

    private sealed class ErrorTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "always fails";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromException<string>(new InvalidOperationException("simulated-tool-failure"));
    }

    private sealed class FakeToolSource(IReadOnlyList<ITool> tools) : IToolSource
    {
        public async IAsyncEnumerable<ITool> DiscoverAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var t in tools)
                yield return t;
        }
    }

    private sealed class FakeToolSourceProvider : INamedToolSourceProvider
    {
        private readonly string _serverId;
        private readonly IToolSource _source;
        internal FakeToolSourceProvider(string serverId, params ITool[] tools)
        {
            _serverId = serverId;
            _source = new FakeToolSource(tools);
        }
        public IToolSource? GetByName(string name)
            => string.Equals(name, _serverId, StringComparison.OrdinalIgnoreCase) ? _source : null;
    }

    private sealed class NoopCompletionProviderPool : ICompletionProviderPool
    {
        public ValueTask<ICompletionProvider> GetAsync(ModelSpec spec, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This test does not exercise LLM routes.");
    }

    private sealed class NoopGuardrail : IToolGuardrail
    {
        public ValueTask<GuardrailOutcome> BeforeInvokeAsync(
            ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Pass);

        public ValueTask<GuardrailOutcome> AfterInvokeAsync(
            ITool tool, JsonElement arguments, string result, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Pass);
    }

    private sealed class CapturingEventBus : IAgentEventBus
    {
        public List<AgentEvent> Events { get; } = [];

        public ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default)
        {
            lock (Events) { Events.Add(@event); }
            return ValueTask.CompletedTask;
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class SingleServerRegistry(McpServerManifest manifest) : IMcpServerRegistry
    {
        public IAsyncEnumerable<McpServerManifest> ListAsync(
            string? labelPrefix = null, CancellationToken ct = default)
            => One(manifest);

        public ValueTask<McpServerManifest?> GetAsync(
            string id, string? version = null, CancellationToken ct = default)
            => ValueTask.FromResult<McpServerManifest?>(
                string.Equals(id, manifest.Id, StringComparison.OrdinalIgnoreCase) ? manifest : null);

        public ValueTask RegisterAsync(McpServerManifest m, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<McpServerManifest> One(McpServerManifest m)
        {
            await Task.CompletedTask;
            yield return m;
        }
    }
}
