// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Vais.Agents.Control;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Gateways.OpenAiCompat.Tests;

/// <summary>
/// OCR-1..OCR-7 — caller-supplied <c>X-Run-Id</c> run correlation across the LLM,
/// <c>agent:</c> and <c>graph:</c> routing paths.
/// </summary>
public sealed class OpenAiCompatCorrelationTests
{
    private const string LlmModel = "gpt-test";

    // ── Host builder ─────────────────────────────────────────────────────────

    private static Task<IHost> BuildHostAsync(
        Action<IServiceCollection> configureServices,
        AgentContext? identity = null)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddOpenAiCompatGateway();
                    services.AddPassThroughIdentityResolver(identity);
                    services.AddInMemoryModelRouter(_ => { }); // default; ConfigureLlm overrides with a real route
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

    private static HttpClient ClientWithRunId(IHost host, string? runId, bool skipValidation = false)
    {
        var http = host.GetTestClient();
        if (runId is not null)
        {
            if (skipValidation)
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Run-Id", runId);
            else
                http.DefaultRequestHeaders.Add("X-Run-Id", runId);
        }
        return http;
    }

    // ── LLM path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LlmPath_StampsRunIdFromHeader()
    {
        var host = await BuildHostAsync(ConfigureLlm);
        var capture = SingleCapture(host);
        using var http = ClientWithRunId(host, "run-abc");

        try
        {
            var response = await PostLlmAsync(http);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capture.RunId.Should().Be("run-abc");
            capture.CorrelationId.Should().Be("run-abc");
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task LlmPath_NoHeader_LeavesRunIdNull()
    {
        var host = await BuildHostAsync(ConfigureLlm);
        var capture = SingleCapture(host);
        using var http = ClientWithRunId(host, null);

        try
        {
            var response = await PostLlmAsync(http);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capture.Captured.Should().BeTrue();
            capture.RunId.Should().BeNull();
            capture.CorrelationId.Should().BeNull();
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task LlmPath_HeaderOverridesIdentityRunId_PreservesIdentityCorrelation()
    {
        var identity = new AgentContext(CorrelationId: "id-corr") { RunId = "id-run" };
        var host = await BuildHostAsync(ConfigureLlm, identity);
        var capture = SingleCapture(host);
        using var http = ClientWithRunId(host, "hdr-run");

        try
        {
            var response = await PostLlmAsync(http);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capture.RunId.Should().Be("hdr-run", "an explicit header overrides the identity-derived run id");
            capture.CorrelationId.Should().Be("id-corr", "a non-null identity correlation id is never overwritten by the run id");
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task LlmPath_FillsCorrelationIdWhenNull()
    {
        var host = await BuildHostAsync(ConfigureLlm); // Empty identity → CorrelationId null
        var capture = SingleCapture(host);
        using var http = ClientWithRunId(host, "r");

        try
        {
            var response = await PostLlmAsync(http);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capture.RunId.Should().Be("r");
            capture.CorrelationId.Should().Be("r");
        }
        finally { await StopAsync(host); }
    }

    [Theory]
    [InlineData("run with space")]            // whitespace → rejected
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 201 chars → rejected
    public async Task LlmPath_InvalidHeaderIgnored(string badRunId)
    {
        var host = await BuildHostAsync(ConfigureLlm);
        var capture = SingleCapture(host);
        using var http = ClientWithRunId(host, badRunId, skipValidation: true);

        try
        {
            var response = await PostLlmAsync(http);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            capture.RunId.Should().BeNull("an invalid X-Run-Id falls through to the identity / mint path");
        }
        finally { await StopAsync(host); }
    }

    // ── agent: path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentNonStreaming_UsesHeaderAsSessionId()
    {
        var lifecycle = new RecordingAgentLifecycleManager();
        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentRegistry>(new SingleAgentRegistry("myagent"));
            services.AddSingleton<IAgentLifecycleManager>(lifecycle);
        });
        using var http = ClientWithRunId(host, "sess-xyz");

        try
        {
            var response = await PostAgentAsync(http, stream: false);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            lifecycle.LastRequest!.SessionId.Should().Be("sess-xyz");
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task AgentNonStreaming_NoHeader_MintsSessionId()
    {
        var lifecycle = new RecordingAgentLifecycleManager();
        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentRegistry>(new SingleAgentRegistry("myagent"));
            services.AddSingleton<IAgentLifecycleManager>(lifecycle);
        });
        using var http = ClientWithRunId(host, null);

        try
        {
            var response = await PostAgentAsync(http, stream: false);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            lifecycle.LastRequest!.SessionId.Should().NotBeNullOrEmpty();
            lifecycle.LastRequest!.SessionId.Should().HaveLength(32, "a minted session id is a 32-char hex GUID");
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task AgentStreaming_WithHeader_UsesGetOrCreateForSession()
    {
        var runtime = new RecordingAgentRuntime();
        var host = await BuildHostAsync(services => services.AddSingleton<IAgentRuntime>(runtime));
        using var http = ClientWithRunId(host, "sess-stream");

        try
        {
            var response = await PostAgentAsync(http, stream: true);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            runtime.LastSessionId.Should().Be("sess-stream");
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task AgentStreaming_NoHeader_UsesGetOrCreate()
    {
        var runtime = new RecordingAgentRuntime();
        var host = await BuildHostAsync(services => services.AddSingleton<IAgentRuntime>(runtime));
        using var http = ClientWithRunId(host, null);

        try
        {
            var response = await PostAgentAsync(http, stream: true);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            runtime.SessionRequested.Should().BeFalse("without a header the endpoint must not scope to a session");
            runtime.LastSessionId.Should().BeNull();
        }
        finally { await StopAsync(host); }
    }

    // ── graph: path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GraphNonStreaming_UsesHeaderAsRunId()
    {
        var lifecycle = new RecordingGraphLifecycleManager();
        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentGraphRegistry>(new SingleGraphRegistry("research"));
            services.AddSingleton<IAgentGraphLifecycleManager>(lifecycle);
        });
        using var http = ClientWithRunId(host, "graph-run-1");

        try
        {
            var response = await PostGraphAsync(http, stream: false);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            lifecycle.LastRequest!.RunId.Should().Be("graph-run-1");
        }
        finally { await StopAsync(host); }
    }

    [Fact]
    public async Task GraphStreaming_UsesHeaderAsRunId()
    {
        var lifecycle = new RecordingGraphLifecycleManager();
        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentGraphRegistry>(new SingleGraphRegistry("research"));
            services.AddSingleton<IAgentGraphLifecycleManager>(lifecycle);
        });
        using var http = ClientWithRunId(host, "graph-run-2");

        try
        {
            var response = await PostGraphAsync(http, stream: true);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            lifecycle.LastRequest!.RunId.Should().Be("graph-run-2");
        }
        finally { await StopAsync(host); }
    }

    // ── Request helpers ───────────────────────────────────────────────────────

    private static void ConfigureLlm(IServiceCollection services)
    {
        var route = new ModelRoute(new FakeProvider(), new ModelSpec("custom", LlmModel));
        services.AddInMemoryModelRouter(routes => routes.Add(LlmModel, route));
        services.AddSingleton<LlmGatewayMiddleware>(sp =>
            new CapturingMiddleware(sp.GetRequiredService<IAgentContextAccessor>()));
    }

    private static CapturingMiddleware SingleCapture(IHost host) =>
        host.Services.GetServices<LlmGatewayMiddleware>().OfType<CapturingMiddleware>().Single();

    private static Task<HttpResponseMessage> PostLlmAsync(HttpClient http) =>
        http.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = LlmModel,
            messages = new[] { new { role = "user", content = "hi" } }
        });

    private static Task<HttpResponseMessage> PostAgentAsync(HttpClient http, bool stream) =>
        http.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "agent:myagent",
            stream,
            messages = new[] { new { role = "user", content = "hi" } }
        });

    private static Task<HttpResponseMessage> PostGraphAsync(HttpClient http, bool stream) =>
        http.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "graph:research",
            stream,
            messages = new[] { new { role = "user", content = "hi" } }
        });

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class CapturingMiddleware(IAgentContextAccessor ctx) : LlmGatewayMiddleware
    {
        public bool Captured { get; private set; }
        public string? RunId { get; private set; }
        public string? CorrelationId { get; private set; }

        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
        {
            Captured = true;
            RunId = ctx.Current.RunId;
            CorrelationId = ctx.Current.CorrelationId;
            return next(request, cancellationToken);
        }
    }

    private sealed class FakeProvider : ICompletionProvider
    {
        public string ProviderName => "Fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse("ok"));
    }

    private sealed class SingleAgentRegistry(string id) : IAgentRegistry
    {
        private readonly AgentManifest _manifest = new(
            Id: id, Version: "1.0.0", Handler: new AgentHandlerRef("FakeAgent"), Protocols: [], Tools: []);

        public async IAsyncEnumerable<AgentManifest> ListAsync(
            string? labelPrefix = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return _manifest;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => new(id == _manifest.Id ? _manifest : null);
    }

    private sealed class RecordingAgentLifecycleManager : IAgentLifecycleManager
    {
        public AgentInvocationRequest? LastRequest { get; private set; }

        public ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
            => new(new AgentHandle(manifest.Id, manifest.Version));

        public ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return new(new AgentInvocationResult("ok"));
        }

        public ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default) => default;
        public ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default) => new(AgentStatus.Active);
        public ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default) => default;
        public ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default) => new(handle);
        public ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default) => default;
    }

    private sealed class RecordingAgentRuntime : IAgentRuntime
    {
        public bool SessionRequested { get; private set; }
        public string? LastSessionId { get; private set; }

        public IAiAgent GetOrCreate(string agentId) => new StubStreamingAgent();

        public IAiAgent GetOrCreateForSession(string agentId, string sessionId)
        {
            SessionRequested = true;
            LastSessionId = sessionId;
            return new StubStreamingAgent();
        }

        public bool TryGet(string agentId, out IAiAgent? agent) { agent = null; return false; }
        public bool Remove(string agentId) => false;
        public bool RemoveSession(string agentId, string sessionId) => false;
    }

    private sealed class StubStreamingAgent : IAiAgent, IStreamingAiAgent
    {
        public string? SystemPrompt { get; set; }
        public IAgentSession Session => throw new NotSupportedException();
        public IReadOnlyList<ChatTurn> History => [];
        public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default) => Task.FromResult("ok");
        public void Reset() { }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            string userMessage,
            AgentContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            yield return new CompletionDelta(now, context, "ok");
            yield return new TurnCompleted(now, context, "ok", null, null, null, TimeSpan.Zero);
        }
#pragma warning restore CS1998
    }

    private sealed class SingleGraphRegistry(string id) : IAgentGraphRegistry
    {
        private readonly AgentGraphManifest _manifest = new(
            Id: id,
            Version: "1.0.0",
            Entry: "start",
            Nodes: [new GraphNode("start", "End")],
            Edges: [],
            Annotations: new Dictionary<string, string>
            {
                ["vais.io/openai-compat-input-key"] = "messages",
                ["vais.io/openai-compat-output-key"] = "output"
            });

        public async IAsyncEnumerable<AgentGraphManifest> ListAsync(
            string? labelPrefix = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return _manifest;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask<AgentGraphManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => new(id == _manifest.Id ? _manifest : null);
    }

    private sealed class RecordingGraphLifecycleManager : IAgentGraphLifecycleManager
    {
        public GraphInvocationRequest? LastRequest { get; private set; }

        private static GraphInvocationResult Result()
        {
            var state = new Dictionary<string, JsonElement>
            {
                ["output"] = JsonSerializer.SerializeToElement("done")
            };
            return new GraphInvocationResult("run", state, true);
        }

        public ValueTask<AgentGraphHandle> CreateAsync(AgentGraphManifest manifest, CancellationToken ct = default)
            => new(new AgentGraphHandle(manifest.Id, manifest.Version));

        public ValueTask<AgentGraphHandle> UpdateAsync(AgentGraphHandle handle, AgentGraphManifest newManifest, CancellationToken ct = default)
            => new(handle);

        public ValueTask<AgentGraphStatus> QueryAsync(AgentGraphHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<GraphInvocationResult> InvokeAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return new(Result());
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentGraphEvent> InvokeStreamAsync(
            AgentGraphHandle handle,
            GraphInvocationRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            var now = DateTimeOffset.UtcNow;
            yield return new GraphCompleted(now, AgentContext.Empty, "run", 1, "end", TimeSpan.Zero);
        }
#pragma warning restore CS1998

        public ValueTask<GraphInvocationResult> ResumeAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask CancelAsync(AgentGraphHandle handle, string runId, CancellationToken ct = default) => default;
        public ValueTask EvictAsync(AgentGraphHandle handle, CancellationToken ct = default) => default;
    }
}
