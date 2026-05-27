// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
/// HTTP-level tests for <c>MapContainerGatewayEndpoints</c>. Covers G1+G4 wiring:
/// each LLM endpoint pushes an <see cref="AgentContext"/> from the
/// <c>X-Run-Id</c>/<c>X-Agent-Id</c> headers, traverses every registered
/// <see cref="LlmGatewayMiddleware"/>, and supports both streaming and non-streaming
/// <c>chat/completions</c>.
/// </summary>
public sealed class ContainerGatewayEndpointTests : IAsyncLifetime
{
    private const string TestSecret = "A32CharacterSecretKeyForTestingXX";

    private IHost _host = null!;
    private HttpClient _client = null!;
    private RecordingMiddleware _middleware = null!;
    private FakeProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _middleware = new RecordingMiddleware();
        _provider = new FakeProvider();

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

                    services.AddSingleton<ICompletionProviderPool>(new FakeProviderPool(_provider));
                    services.AddSingleton<LlmGatewayMiddleware>(_middleware);
                    services.AddSingleton<IAgentManifestTranslator, PermissiveFakeTranslator>();

                    // Required so minimal-API binding can identify the tools/invoke handler
                    // service parameters. These endpoints are mapped by MapContainerGatewayEndpoints
                    // even though our LLM tests don't call them; the binder needs to resolve the
                    // shape at startup.
                    services.AddSingleton<IMcpServerRegistry, EmptyMcpServerRegistry>();

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
    public async Task ChatCompletions_NonStreaming_TraversesMiddleware_AndPushesRunIdContext()
    {
        var (runId, agentId) = ("run-abc", "agent-xyz");
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

        _middleware.NonStreamInvocations.Should().Be(1);
        _middleware.LastRunIdSeen.Should().Be(runId);
    }

    [Fact]
    public async Task ChatCompletions_Streaming_EmitsSseFrames_AndRecordsMiddlewareCompletion()
    {
        _provider.StreamingChunks = new[] { "alpha ", "beta ", "gamma" };
        var (runId, agentId) = ("run-stream", "agent-stream");
        var token = MintToken(runId, agentId);

        var body = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "go" } },
            stream = true,
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        var responseText = await resp.Content.ReadAsStringAsync();

        // Each chunk should appear as its own SSE data line, plus a final [DONE].
        responseText.Should().Contain("\"content\":\"alpha \"");
        responseText.Should().Contain("\"content\":\"beta \"");
        responseText.Should().Contain("\"content\":\"gamma\"");
        responseText.Should().Contain("\"finish_reason\":\"stop\"");
        responseText.Should().Contain("data: [DONE]");

        _middleware.StreamCompletions.Should().Be(1);
        _middleware.LastRunIdSeen.Should().Be(runId);
    }

    [Fact]
    public async Task LlmComplete_TraversesMiddleware_AndPushesRunIdContext()
    {
        var (runId, agentId) = ("run-legacy", "agent-legacy");
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

        _middleware.NonStreamInvocations.Should().Be(1);
        _middleware.LastRunIdSeen.Should().Be(runId);
    }

    [Fact]
    public async Task ChatCompletions_MissingBearer_Returns401()
    {
        var body = new
        {
            model = "gpt-4o-mini",
            messages = new[] { new { role = "user", content = "hi" } },
        };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/chat/completions")
        {
            Content = JsonContent.Create(body),
        };

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _middleware.NonStreamInvocations.Should().Be(0);
    }

    [Fact]
    public async Task TokenRenew_ValidToken_ReturnsFreshTokenForSameIdentity()
    {
        var (runId, agentId) = ("run-renew", "agent-renew");
        var token = MintToken(runId, agentId);

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/token/renew");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<RenewBody>();
        body!.Token.Should().NotBeNullOrEmpty();
        body.Token.Should().NotBe(token, "renewal mints a fresh token with a later expiry");
        _host.Services.GetRequiredService<ICallTokenService>()
            .Validate(body.Token, runId, agentId).Should().BeTrue();
    }

    [Fact]
    public async Task TokenRenew_ExpiredToken_Returns401()
    {
        var (runId, agentId) = ("run-exp", "agent-exp");
        var expired = _host.Services.GetRequiredService<ICallTokenService>().Generate(runId, agentId, -1);

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/token/renew");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expired);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record RenewBody(string Token, long ExpiresAt);

    // ── Phase 3: lease-bound (v2) tokens ──────────────────────────────────────

    [Fact]
    public async Task LlmComplete_SessionToken_LiveLease_Succeeds()
    {
        var (runId, agentId) = ("run-live", "agent-live");
        var leaseId = await OpenLeaseAsync(runId, agentId);
        var token = MintSessionToken(runId, agentId, leaseId);

        var resp = await PostLlmComplete(token, runId, agentId);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LlmComplete_SessionToken_ReleasedLease_Returns401()
    {
        var (runId, agentId) = ("run-dead", "agent-dead");
        var leaseId = await OpenLeaseAsync(runId, agentId);
        await _host.Services.GetRequiredService<IInvokeLeaseStore>().ReleaseAsync(leaseId);
        var token = MintSessionToken(runId, agentId, leaseId);

        var resp = await PostLlmComplete(token, runId, agentId);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _middleware.NonStreamInvocations.Should().Be(0);
    }

    [Fact]
    public async Task TokenRenew_SessionToken_LiveLease_ReturnsSessionToken()
    {
        var (runId, agentId) = ("run-renew2", "agent-renew2");
        var leaseId = await OpenLeaseAsync(runId, agentId);
        var token = MintSessionToken(runId, agentId, leaseId);

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/token/renew");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<RenewBody>();
        _host.Services.GetRequiredService<ICallTokenService>()
            .TryExtract(body!.Token, out _, out _, out var renewedLeaseId).Should().BeTrue();
        renewedLeaseId.Should().Be(leaseId, "the renewed token carries the same lease");
    }

    [Fact]
    public async Task TokenRenew_SessionToken_ReleasedLease_Returns401()
    {
        var (runId, agentId) = ("run-renewdead", "agent-renewdead");
        var leaseId = await OpenLeaseAsync(runId, agentId);
        await _host.Services.GetRequiredService<IInvokeLeaseStore>().ReleaseAsync(leaseId);
        var token = MintSessionToken(runId, agentId, leaseId);

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/token/renew");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);

        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<string> OpenLeaseAsync(string runId, string agentId)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        await _host.Services.GetRequiredService<IInvokeLeaseStore>()
            .StartAsync(leaseId, runId, agentId, sessionTtlSeconds: 300, heartbeatTtlSeconds: 180);
        return leaseId;
    }

    private string MintSessionToken(string runId, string agentId, string leaseId)
        => _host.Services.GetRequiredService<ICallTokenService>().Generate(runId, agentId, leaseId, 60);

    private async Task<HttpResponseMessage> PostLlmComplete(string token, string runId, string agentId)
    {
        var body = new { modelId = "gpt-4o-mini", messages = new[] { new { role = "user", content = "hi" } } };
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/container-gateway/llm/complete")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Run-Id", runId);
        req.Headers.Add("X-Agent-Id", agentId);
        return await _client.SendAsync(req);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string MintToken(string runId, string agentId)
    {
        var svc = _host.Services.GetRequiredService<ICallTokenService>();
        return svc.Generate(runId, agentId, 60);
    }

    private sealed class EmptyMcpServerRegistry : IMcpServerRegistry
    {
        public IAsyncEnumerable<McpServerManifest> ListAsync(
            string? labelPrefix = null, CancellationToken ct = default)
            => Empty<McpServerManifest>();

        public ValueTask<McpServerManifest?> GetAsync(
            string id, string? version = null, CancellationToken ct = default)
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

    private sealed class FakeProviderPool(FakeProvider provider) : ICompletionProviderPool
    {
        public ValueTask<ICompletionProvider> GetAsync(ModelSpec spec, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<ICompletionProvider>(provider);
    }

    private sealed class FakeProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        public IReadOnlyList<string> StreamingChunks { get; set; } = ["ok"];

        public string ProviderName => "fake";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse("ok", "fake-model", PromptTokens: 1, CompletionTokens: 1));

        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
            CompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in StreamingChunks)
            {
                yield return new CompletionUpdate(chunk, ModelId: "fake-model");
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingMiddleware : LlmGatewayMiddleware
    {
        public int NonStreamInvocations;
        public int StreamCompletions;
        public string? LastRunIdSeen;

        protected override async Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref NonStreamInvocations);
            LastRunIdSeen = new AsyncLocalAgentContextAccessor().Current.RunId;
            return await next(request, cancellationToken);
        }

        protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            CancellationToken cancellationToken)
        {
            LastRunIdSeen = new AsyncLocalAgentContextAccessor().Current.RunId;
            return next(request, cancellationToken);
        }

        protected override ValueTask OnStreamCompleteAsync(
            CompletionResponse final,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StreamCompletions);
            return ValueTask.CompletedTask;
        }
    }
}
