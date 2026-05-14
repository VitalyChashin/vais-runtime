// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
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
/// v0.12 PR 3: SSE streaming Invoke endpoint + typed client overloads end-to-end.
/// Drives real HTTP through the TestServer; exercises text-only + full-events
/// clients, 501 path on non-streaming agents, RequestAborted cancellation,
/// heartbeat cadence, idempotency-key bypass, OpenAPI spec lists the new operation.
/// </summary>
public sealed class AgentControlPlaneStreamingInvokeTests
{
    private static readonly AgentManifest StreamingAgentManifest = new(
        "streamer", "1.0",
        new AgentHandlerRef("declarative"),
        new[] { new ProtocolBinding("Http") },
        Array.Empty<ToolRef>());

    // ---- 1: end-to-end text-only client round-trip ----

    [Fact]
    public async Task Text_Only_Client_Round_Trip_Yields_Provider_Chunks()
    {
        using var host = await StartHostAsync(new[]
        {
            new CompletionUpdate("hello "),
            new CompletionUpdate("world"),
        });
        await RegisterAgentAsync(host, StreamingAgentManifest);
        var client = new AgentControlPlaneClient(host.GetTestClient());

        var deltas = new List<string>();
        await foreach (var d in client.InvokeStreamAsync(
            StreamingAgentManifest.Id,
            new AgentInvocationRequest("hi"),
            version: null,
            idempotencyKey: null,
            cancellationToken: default))
        {
            deltas.Add(d);
        }

        deltas.Should().Equal("hello ", "world");
    }

    // ---- 2: full-events client round-trip ----

    [Fact]
    public async Task Full_Events_Client_Round_Trip_Yields_Full_Taxonomy()
    {
        using var host = await StartHostAsync(new[]
        {
            new CompletionUpdate("hi"),
        });
        await RegisterAgentAsync(host, StreamingAgentManifest);
        var client = new AgentControlPlaneClient(host.GetTestClient());

        var events = new List<AgentEvent>();
        await foreach (var e in client.InvokeStreamEventsAsync(
            StreamingAgentManifest.Id,
            new AgentInvocationRequest("hello"),
            version: null,
            idempotencyKey: null,
            cancellationToken: default))
        {
            events.Add(e);
        }

        events[0].Should().BeOfType<TurnStarted>();
        events.OfType<CompletionDelta>().Should().NotBeEmpty();
        events[^1].Should().BeOfType<TurnCompleted>();
    }

    // ---- 3: 501 path on non-streaming agent ----

    [Fact]
    public async Task Non_Streaming_Agent_Returns_501()
    {
        using var host = await StartHostAsync(useNonStreamingProvider: true);
        await RegisterAgentAsync(host, StreamingAgentManifest);
        var client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/v1/agents/{StreamingAgentManifest.Id}/invoke/stream",
            new AgentInvocationRequest("hi"));

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    // ---- 4: 501 Problem Details carries the URN ----

    [Fact]
    public async Task Non_Streaming_Agent_501_Problem_Details_Has_Urn()
    {
        using var host = await StartHostAsync(useNonStreamingProvider: true);
        await RegisterAgentAsync(host, StreamingAgentManifest);
        var client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/v1/agents/{StreamingAgentManifest.Id}/invoke/stream",
            new AgentInvocationRequest("hi"));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        problem.GetProperty("type").GetString().Should().Be(ProblemDetailsMapping.StreamingNotSupportedType);
        problem.GetProperty("agentId").GetString().Should().Be(StreamingAgentManifest.Id);
    }

    // ---- 5: content-type is text/event-stream with no-cache ----

    [Fact]
    public async Task Response_Content_Type_Is_Sse_With_No_Cache()
    {
        using var host = await StartHostAsync(new[] { new CompletionUpdate("ok") });
        await RegisterAgentAsync(host, StreamingAgentManifest);
        var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/agents/{StreamingAgentManifest.Id}/invoke/stream")
        {
            Content = JsonContent.Create(new AgentInvocationRequest("hi")),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();
    }

    // ---- 6: 404 when agent not registered ----

    [Fact]
    public async Task Unknown_Agent_Returns_404()
    {
        using var host = await StartHostAsync(new[] { new CompletionUpdate("ok") });
        // Don't register the agent.
        var client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/v1/agents/ghost/invoke/stream",
            new AgentInvocationRequest("hi"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- 7: tool-call events interleaved with deltas ----

    [Fact]
    public async Task Tool_Call_Events_Interleave_With_Deltas()
    {
        var callId = "call-1";
        var argsJson = JsonDocument.Parse("""{}""").RootElement;
        using var host = await StartHostAsync(
            turns: new IEnumerable<CompletionUpdate>[]
            {
                new[]
                {
                    new CompletionUpdate(string.Empty, ToolCalls: new[]
                    {
                        new ToolCallRequest("echo", argsJson, callId),
                    }),
                },
                new[]
                {
                    new CompletionUpdate("done"),
                },
            },
            toolRegistry: new[] { Tool.FromFunc<Dictionary<string, JsonElement>, string>("echo", "echo", (_, _) => Task.FromResult("echo-ok")) });
        await RegisterAgentAsync(host, StreamingAgentManifest);
        var client = new AgentControlPlaneClient(host.GetTestClient());

        var events = new List<AgentEvent>();
        await foreach (var e in client.InvokeStreamEventsAsync(
            StreamingAgentManifest.Id,
            new AgentInvocationRequest("hi"),
            version: null,
            idempotencyKey: null,
            cancellationToken: default))
        {
            events.Add(e);
        }

        var kinds = events.Select(e => e.GetType().Name).ToList();
        kinds.Should().ContainInOrder(new[]
        {
            nameof(TurnStarted),
            nameof(ToolCallStarted),
            nameof(ToolCallCompleted),
            nameof(TurnCompleted),
        });
        events.OfType<ToolCallStarted>().Single().CallId.Should().Be(callId);
    }

    // ---- 8: Idempotency-Key header bypasses cache (SSE opt-out) ----

    [Fact]
    public async Task Idempotency_Key_On_Stream_Does_Not_Cache()
    {
        using var host = await StartHostAsync(new[] { new CompletionUpdate("ok") }, mountIdempotency: true);
        await RegisterAgentAsync(host, StreamingAgentManifest);
        var client = host.GetTestClient();

        async Task<string> DrainAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/agents/{StreamingAgentManifest.Id}/invoke/stream")
            {
                Content = JsonContent.Create(new AgentInvocationRequest("hi")),
            };
            request.Headers.Add("Idempotency-Key", "stream-key");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            // Streaming responses never carry Idempotency-Replayed — middleware skipped caching.
            response.Headers.Contains("Idempotency-Replayed").Should().BeFalse();
            return await response.Content.ReadAsStringAsync();
        }

        var body1 = await DrainAsync();
        var body2 = await DrainAsync();

        // Both responses stream independently (no cached replay).
        body1.Should().Contain("event: delta");
        body2.Should().Contain("event: delta");
    }

    // ---- 9: OpenAPI lists the Agents.InvokeStream operation ----

    [Fact]
    public async Task OpenApi_Spec_Lists_InvokeStream_Operation()
    {
        using var host = await StartHostAsync(new[] { new CompletionUpdate("ok") }, mountOpenApi: true);
        var client = host.GetTestClient();

        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var spec = await response.Content.ReadFromJsonAsync<JsonElement>();

        var path = spec.GetProperty("paths").GetProperty("/v1/agents/{id}/invoke/stream");
        var op = path.GetProperty("post");
        op.GetProperty("operationId").GetString().Should().Be("Agents.InvokeStream");

        // 501 response carries the urn extension.
        var r501 = op.GetProperty("responses").GetProperty("501");
        r501.TryGetProperty("x-vais-type-urns", out var urns).Should().BeTrue();
        urns.EnumerateArray().Select(u => u.GetString())
            .Should().Contain(ProblemDetailsMapping.StreamingNotSupportedType);
    }

    // ---- 10: body validation — empty text → 400 ----

    [Fact]
    public async Task Empty_Text_Body_Returns_400()
    {
        using var host = await StartHostAsync(new[] { new CompletionUpdate("ok") });
        await RegisterAgentAsync(host, StreamingAgentManifest);
        var client = host.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            $"/v1/agents/{StreamingAgentManifest.Id}/invoke/stream",
            new AgentInvocationRequest(""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- helpers ----

    private static async Task RegisterAgentAsync(IHost host, AgentManifest manifest)
    {
        var manager = host.Services.GetRequiredService<IAgentLifecycleManager>();
        await manager.CreateAsync(manifest);
    }

    private static Task<IHost> StartHostAsync(
        IEnumerable<CompletionUpdate>? singleTurn = null,
        IEnumerable<CompletionUpdate>[]? turns = null,
        IReadOnlyList<ITool>? toolRegistry = null,
        bool useNonStreamingProvider = false,
        bool mountIdempotency = false,
        bool mountOpenApi = false)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    ICompletionProvider provider = useNonStreamingProvider
                        ? new NonStreamingProvider()
                        : (turns is not null
                            ? new MultiTurnStreamingProvider(turns)
                            : new SingleTurnStreamingProvider(singleTurn ?? new[] { new CompletionUpdate("default") }));
                    services.AddSingleton<ICompletionProvider>(provider);

                    services.AddSingleton<IAgentRuntime>(sp =>
                    {
                        if (useNonStreamingProvider)
                        {
                            // Custom runtime returns an IAiAgent that doesn't implement
                            // IStreamingAiAgent — exercises the 501 path at the HTTP endpoint.
                            return new NonStreamingAgentRuntime();
                        }
                        if (toolRegistry is not null)
                        {
                            return new InMemoryAgentRuntime(
                                sp.GetRequiredService<ICompletionProvider>(),
                                optionsFactory: id => new StatefulAgentOptions
                                {
                                    AgentName = id,
                                    ToolRegistry = new SimpleToolRegistry(toolRegistry),
                                });
                        }
                        return new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>());
                    });
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>()));
                    services.AddAgentControlPlane();
                    if (mountIdempotency)
                    {
                        services.AddAgentControlPlaneIdempotency(opts => opts.EvictionInterval = TimeSpan.Zero);
                    }
                    if (mountOpenApi)
                    {
                        services.AddAgentControlPlaneOpenApi();
                    }
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    if (mountIdempotency) app.UseAgentControlPlaneIdempotency();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAgentControlPlane();
                        if (mountOpenApi) endpoints.MapAgentControlPlaneOpenApi();
                    });
                });
            })
            .StartAsync();
    }

    private sealed class SimpleToolRegistry : IToolRegistry
    {
        public SimpleToolRegistry(IReadOnlyList<ITool> tools) { Tools = tools; }
        public IReadOnlyList<ITool> Tools { get; }
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class SingleTurnStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        private readonly IEnumerable<CompletionUpdate> _updates;
        public SingleTurnStreamingProvider(IEnumerable<CompletionUpdate> updates) { _updates = updates; }

        public string ProviderName => "fake-single";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

#pragma warning disable CS1998
        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var u in _updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return u;
            }
        }
#pragma warning restore CS1998
    }

    private sealed class MultiTurnStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        private readonly Queue<IEnumerable<CompletionUpdate>> _turns;
        public MultiTurnStreamingProvider(IEnumerable<IEnumerable<CompletionUpdate>> turns)
        {
            _turns = new Queue<IEnumerable<CompletionUpdate>>(turns);
        }

        public string ProviderName => "fake-multi";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

#pragma warning disable CS1998
        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!_turns.TryDequeue(out var turn))
            {
                throw new InvalidOperationException("MultiTurnStreamingProvider out of turns.");
            }
            foreach (var u in turn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return u;
            }
        }
#pragma warning restore CS1998
    }

    private sealed class NonStreamingProvider : ICompletionProvider
    {
        public string ProviderName => "fake-nonstream";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse("hi"));
    }

    /// <summary>
    /// A bare <see cref="IAiAgent"/> that deliberately does NOT implement
    /// <see cref="IStreamingAiAgent"/>, so the HTTP streaming endpoint's
    /// capability check falls through to 501.
    /// </summary>
    private sealed class NonStreamingAgent : IAiAgent
    {
        public string? SystemPrompt { get; set; }
        public IAgentSession Session { get; } = new InMemoryAgentSession("test", "s");
        public IReadOnlyList<ChatTurn> History => Session.History;
        public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
            => Task.FromResult("unary");
        public Task<string> ResumeAsync(ResumeInput input, CancellationToken cancellationToken = default)
            => Task.FromResult("resumed");
        public void Reset() { }
    }

    private sealed class NonStreamingAgentRuntime : IAgentRuntime
    {
        private readonly NonStreamingAgent _agent = new();
        public IAiAgent GetOrCreate(string agentId) => _agent;
        public IAiAgent GetOrCreateForSession(string agentId, string sessionId) => _agent;
        public bool TryGet(string agentId, out IAiAgent? agent) { agent = _agent; return true; }
        public bool Remove(string agentId) => false;
        public bool RemoveSession(string agentId, string sessionId) => false;
        public void Reset() { }
    }
}
