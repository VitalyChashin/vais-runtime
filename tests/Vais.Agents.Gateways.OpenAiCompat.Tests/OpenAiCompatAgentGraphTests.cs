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
/// OC-1 through OC-9 — OpenAI-compatible agent and graph dispatch tests.
/// </summary>
public sealed class OpenAiCompatAgentGraphTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Task<IHost> BuildHostAsync(Action<IServiceCollection> configureServices)
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

    private static async Task<List<string>> ReadSseDataLinesAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        var lines = new List<string>();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                lines.Add(line["data: ".Length..]);
        }
        return lines;
    }

    // ── OC-4: GET /v1/models aggregates agents + graphs ───────────────────────

    [Fact]
    public async Task ModelsEndpoint_IncludesAgentsAndGraphs()
    {
        var agentRegistry = new FakeAgentRegistry(
        [
            MakeAgentManifest("foo"),
            MakeAgentManifest("bar")
        ]);

        var graphRegistry = new FakeAgentGraphRegistry(
        [
            MakeGraphManifest("research", annotated: true),
            MakeGraphManifest("support", annotated: false)
        ]);

        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentRegistry>(agentRegistry);
            services.AddSingleton<IAgentGraphRegistry>(graphRegistry);
        });

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
            ids.Should().Contain("agent:bar");
            ids.Should().Contain("graph:research");
            ids.Should().NotContain("graph:support");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── OC-6: Agent non-streaming — stateless history injection ───────────────

    [Fact]
    public async Task AgentDispatch_NonStreaming_StatelessHistory()
    {
        var lifecycleManager = new FakeAgentLifecycleManager(
            (_, _) => new AgentInvocationResult("Hello!", "sess-1"));

        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentRegistry>(new FakeAgentRegistry([MakeAgentManifest("myagent")]));
            services.AddSingleton<IAgentLifecycleManager>(lifecycleManager);
        });

        using var http = host.GetTestClient();

        try
        {
            var body = new
            {
                model = "agent:myagent",
                messages = new[]
                {
                    new { role = "user",      content = "hi" },
                    new { role = "assistant", content = "hey" },
                    new { role = "user",      content = "follow-up" }
                }
            };

            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString().Should().Be("Hello!");

            var captured = lifecycleManager.LastRequest;
            captured.Should().NotBeNull();
            captured!.Text.Should().Be("follow-up");
            captured.InitialHistory.Should().NotBeNull();
            captured.InitialHistory!.Should().HaveCount(2);
            captured.InitialHistory![0].Role.Should().Be("user");
            captured.InitialHistory![0].Content.Should().Be("hi");
            captured.InitialHistory![1].Role.Should().Be("assistant");
            captured.InitialHistory![1].Content.Should().Be("hey");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── OC-7: Agent streaming — emits CompletionDelta chunks as SSE ──────────

    [Fact]
    public async Task AgentDispatch_Streaming_EmitsCompletionDeltaChunks()
    {
        var now = DateTimeOffset.UtcNow;
        var ctx = AgentContext.Empty;
        var events = new AgentEvent[]
        {
            new CompletionDelta(now, ctx, "Hello"),
            new CompletionDelta(now, ctx, " world"),
            new TurnCompleted(now, ctx, "Hello world", null, null, null, TimeSpan.Zero)
        };

        var runtime = new FakeStreamingAgentRuntime(events);
        var host = await BuildHostAsync(services => services.AddSingleton<IAgentRuntime>(runtime));
        using var http = host.GetTestClient();

        try
        {
            var body = new
            {
                model = "agent:myagent",
                stream = true,
                messages = new[] { new { role = "user", content = "hi" } }
            };

            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

            var lines = await ReadSseDataLinesAsync(response);
            lines.Should().Contain("[DONE]");

            var dataLines = lines.Where(l => l != "[DONE]").ToList();
            // role header + 2 content deltas + stop chunk
            dataLines.Should().HaveCount(4);

            var firstChunk = JsonSerializer.Deserialize<JsonElement>(dataLines[0]);
            firstChunk.GetProperty("choices")[0].GetProperty("delta")
                .GetProperty("role").GetString().Should().Be("assistant");

            JsonSerializer.Deserialize<JsonElement>(dataLines[1])
                .GetProperty("choices")[0].GetProperty("delta")
                .GetProperty("content").GetString().Should().Be("Hello");

            JsonSerializer.Deserialize<JsonElement>(dataLines[2])
                .GetProperty("choices")[0].GetProperty("delta")
                .GetProperty("content").GetString().Should().Be(" world");

            JsonSerializer.Deserialize<JsonElement>(dataLines[3])
                .GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── OC-7 fallback: non-streaming agent emits single SSE chunk ─────────────

    [Fact]
    public async Task AgentDispatch_Streaming_FallbackWhenNotIStreamingAiAgent()
    {
        var runtime = new FakeNonStreamingAgentRuntime("fallback reply");
        var host = await BuildHostAsync(services => services.AddSingleton<IAgentRuntime>(runtime));
        using var http = host.GetTestClient();

        try
        {
            var body = new
            {
                model = "agent:myagent",
                stream = true,
                messages = new[] { new { role = "user", content = "hi" } }
            };

            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

            var lines = await ReadSseDataLinesAsync(response);
            lines.Should().Contain("[DONE]");

            var dataLines = lines.Where(l => l != "[DONE]").ToList();
            // role header + content chunk + stop chunk
            dataLines.Should().HaveCount(3);

            var contentChunk = JsonSerializer.Deserialize<JsonElement>(dataLines[1]);
            contentChunk.GetProperty("choices")[0].GetProperty("delta")
                .GetProperty("content").GetString().Should().Be("fallback reply");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── OC-8: Graph non-streaming — input/output key mapping ─────────────────

    [Fact]
    public async Task GraphDispatch_NonStreaming_MapsInputOutputKeys()
    {
        var outputEl = JsonSerializer.SerializeToElement("graph result");
        var finalState = new Dictionary<string, JsonElement> { ["output"] = outputEl };

        var lifecycleManager = new FakeAgentGraphLifecycleManager(
            (_, _) => new GraphInvocationResult("run-1", finalState, true));

        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentGraphRegistry>(
                new FakeAgentGraphRegistry([MakeGraphManifest("research", annotated: true)]));
            services.AddSingleton<IAgentGraphLifecycleManager>(lifecycleManager);
        });

        using var http = host.GetTestClient();

        try
        {
            var body = new
            {
                model = "graph:research",
                messages = new[] { new { role = "user", content = "research this" } }
            };

            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString().Should().Be("graph result");

            lifecycleManager.LastRequest.Should().NotBeNull();
            lifecycleManager.LastRequest!.InitialState.Should().ContainKey("messages");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── OC-8: Missing output key returns 500 ─────────────────────────────────

    [Fact]
    public async Task GraphDispatch_NonStreaming_MissingOutputKey_Returns500()
    {
        var lifecycleManager = new FakeAgentGraphLifecycleManager(
            (_, _) => new GraphInvocationResult("run-1", new Dictionary<string, JsonElement>(), true));

        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentGraphRegistry>(
                new FakeAgentGraphRegistry([MakeGraphManifest("research", annotated: true)]));
            services.AddSingleton<IAgentGraphLifecycleManager>(lifecycleManager);
        });

        using var http = host.GetTestClient();

        try
        {
            var body = new
            {
                model = "graph:research",
                messages = new[] { new { role = "user", content = "hi" } }
            };

            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("type").GetString().Should().Be("server_error");
            json.GetProperty("error").GetProperty("message").GetString().Should().Contain("output");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── OC-9: Graph streaming — emits NodeAgentInvoked output as SSE ─────────

    [Fact]
    public async Task GraphDispatch_Streaming_EmitsNodeOutputAsChunks()
    {
        var now = DateTimeOffset.UtcNow;
        var ctx = AgentContext.Empty;
        var graphEvents = new AgentGraphEvent[]
        {
            new NodeAgentInvoked(now, ctx, "run-1", 0, "n1", "agent1", "hi", "Step 1", 0, 0),
            new NodeAgentInvoked(now, ctx, "run-1", 1, "n2", "agent2", "hi", " done",  0, 0),
            new GraphCompleted(now, ctx, "run-1", 2, "end", TimeSpan.Zero)
        };

        var lifecycleManager = new FakeAgentGraphLifecycleManager(graphEvents: graphEvents);

        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentGraphRegistry>(
                new FakeAgentGraphRegistry([MakeGraphManifest("research", annotated: true)]));
            services.AddSingleton<IAgentGraphLifecycleManager>(lifecycleManager);
        });

        using var http = host.GetTestClient();

        try
        {
            var body = new
            {
                model = "graph:research",
                stream = true,
                messages = new[] { new { role = "user", content = "hi" } }
            };

            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

            var lines = await ReadSseDataLinesAsync(response);
            lines.Should().Contain("[DONE]");

            var dataLines = lines.Where(l => l != "[DONE]").ToList();
            // role header + 2 content chunks + stop chunk
            dataLines.Should().HaveCount(4);

            JsonSerializer.Deserialize<JsonElement>(dataLines[1])
                .GetProperty("choices")[0].GetProperty("delta")
                .GetProperty("content").GetString().Should().Be("Step 1");

            JsonSerializer.Deserialize<JsonElement>(dataLines[2])
                .GetProperty("choices")[0].GetProperty("delta")
                .GetProperty("content").GetString().Should().Be(" done");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── OC-4 + OC-8: Unannotated graph absent from /v1/models + returns 422 ──

    [Fact]
    public async Task GraphDispatch_UnannotatedGraph_Returns422()
    {
        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentGraphRegistry>(
                new FakeAgentGraphRegistry([MakeGraphManifest("bare", annotated: false)]));
            services.AddSingleton<IAgentGraphLifecycleManager>(
                new FakeAgentGraphLifecycleManager());
        });

        using var http = host.GetTestClient();

        try
        {
            // /v1/models should NOT include graph:bare
            var modelsResponse = await http.GetAsync("/v1/models");
            var json = await modelsResponse.Content.ReadFromJsonAsync<JsonElement>();
            var ids = json.GetProperty("data").EnumerateArray()
                .Select(el => el.GetProperty("id").GetString()).ToArray();
            ids.Should().NotContain("graph:bare");

            // Directly calling graph:bare → 422 (annotation missing)
            var body = new { model = "graph:bare", messages = new[] { new { role = "user", content = "hi" } } };
            var completionResponse = await http.PostAsJsonAsync("/v1/chat/completions", body);
            completionResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── OC-1 + OC-2: InitialHistory seeds StatefulAiAgent chat history ────────

    [Fact]
    public async Task InitialHistory_SeedsAgentChatHistory()
    {
        CompletionRequest? captured = null;
        var provider = new SyncFakeProvider(req =>
        {
            captured = req;
            return new CompletionResponse("reply");
        });

        var agent = new StatefulAiAgent(provider);
        var request = new AgentInvocationRequest(
            Text: "follow-up",
            InitialHistory:
            [
                ("user", "hello"),
                ("assistant", "hi")
            ]);

        await agent.InvokeAsync(request);

        captured.Should().NotBeNull();
        captured!.History.Should().HaveCount(3);
        captured.History[0].Role.Should().Be(AgentChatRole.User);
        captured.History[0].Text.Should().Be("hello");
        captured.History[1].Role.Should().Be(AgentChatRole.Assistant);
        captured.History[1].Text.Should().Be("hi");
        captured.History[2].Role.Should().Be(AgentChatRole.User);
        captured.History[2].Text.Should().Be("follow-up");
    }

    // ── OC-5: Caller params forwarded as Metadata ─────────────────────────────

    [Fact]
    public async Task CallerParams_ForwardedAsMetadata()
    {
        var lifecycleManager = new FakeAgentLifecycleManager(
            (_, _) => new AgentInvocationResult("ok"));

        var host = await BuildHostAsync(services =>
        {
            services.AddSingleton<IAgentRegistry>(new FakeAgentRegistry([MakeAgentManifest("myagent")]));
            services.AddSingleton<IAgentLifecycleManager>(lifecycleManager);
        });

        using var http = host.GetTestClient();

        try
        {
            var body = new
            {
                model = "agent:myagent",
                temperature = 0.7,
                max_tokens = 100,
                messages = new[] { new { role = "user", content = "hi" } }
            };

            var response = await http.PostAsJsonAsync("/v1/chat/completions", body);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var captured = lifecycleManager.LastRequest;
            captured.Should().NotBeNull();
            captured!.Metadata.Should().NotBeNull();
            captured.Metadata!["oai.temperature"].Should().Be("0.7");
            captured.Metadata!["oai.max_tokens"].Should().Be("100");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
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

    private sealed class FakeAgentRegistry(IReadOnlyList<AgentManifest> manifests) : IAgentRegistry
    {
        public async IAsyncEnumerable<AgentManifest> ListAsync(
            string? labelPrefix = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var m in manifests)
                yield return m;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask<AgentManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => new(manifests.FirstOrDefault(m => m.Id == id));
    }

    private sealed class FakeAgentLifecycleManager(
        Func<AgentHandle, AgentInvocationRequest, AgentInvocationResult>? invoke = null) : IAgentLifecycleManager
    {
        public AgentInvocationRequest? LastRequest { get; private set; }

        public ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
            => new(new AgentHandle(manifest.Id, manifest.Version));

        public ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return new(invoke!(handle, request));
        }

        public ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default) => default;
        public ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default) => new(AgentStatus.Active);
        public ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default) => default;
        public ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default) => new(handle);
        public ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default) => default;
    }

    private sealed class FakeStreamingAgentRuntime(IReadOnlyList<AgentEvent> events) : IAgentRuntime
    {
        public IAiAgent GetOrCreate(string id) => new FakeStreamingAgent(events);
        public IAiAgent GetOrCreateForSession(string id, string sessionId) => new FakeStreamingAgent(events);
        public bool TryGet(string id, out IAiAgent? agent) { agent = null; return false; }
        public bool Remove(string id) => false;
        public bool RemoveSession(string id, string sessionId) => false;
    }

    private sealed class FakeStreamingAgent(IReadOnlyList<AgentEvent> events) : IAiAgent, IStreamingAiAgent
    {
        public string? SystemPrompt { get; set; }
        public IAgentSession Session => throw new NotSupportedException();
        public IReadOnlyList<ChatTurn> History => [];
        public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Reset() { }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentEvent> StreamAsync(
            string userMessage,
            AgentContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var evt in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }
        }
#pragma warning restore CS1998
    }

    private sealed class FakeNonStreamingAgentRuntime(string reply) : IAgentRuntime
    {
        public IAiAgent GetOrCreate(string id) => new FakeNonStreamingAgent(reply);
        public IAiAgent GetOrCreateForSession(string id, string sessionId) => new FakeNonStreamingAgent(reply);
        public bool TryGet(string id, out IAiAgent? agent) { agent = null; return false; }
        public bool Remove(string id) => false;
        public bool RemoveSession(string id, string sessionId) => false;
    }

    private sealed class FakeNonStreamingAgent(string reply) : IAiAgent
    {
        public string? SystemPrompt { get; set; }
        public IAgentSession Session => throw new NotSupportedException();
        public IReadOnlyList<ChatTurn> History => [];
        public Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default) => Task.FromResult(reply);
        public void Reset() { }
    }

    private sealed class FakeAgentGraphRegistry(IReadOnlyList<AgentGraphManifest> manifests) : IAgentGraphRegistry
    {
        public async IAsyncEnumerable<AgentGraphManifest> ListAsync(
            string? labelPrefix = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var m in manifests)
                yield return m;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public ValueTask<AgentGraphManifest?> GetAsync(string id, string? version = null, CancellationToken cancellationToken = default)
            => new(manifests.FirstOrDefault(m => m.Id == id));
    }

    private sealed class FakeAgentGraphLifecycleManager(
        Func<AgentGraphHandle, GraphInvocationRequest, GraphInvocationResult>? invoke = null,
        IReadOnlyList<AgentGraphEvent>? graphEvents = null) : IAgentGraphLifecycleManager
    {
        public GraphInvocationRequest? LastRequest { get; private set; }

        public ValueTask<AgentGraphHandle> CreateAsync(AgentGraphManifest manifest, CancellationToken ct = default)
            => new(new AgentGraphHandle(manifest.Id, manifest.Version));

        public ValueTask<AgentGraphHandle> UpdateAsync(AgentGraphHandle handle, AgentGraphManifest newManifest, CancellationToken ct = default)
            => new(handle);

        public ValueTask<AgentGraphStatus> QueryAsync(AgentGraphHandle handle, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<GraphInvocationResult> InvokeAsync(AgentGraphHandle handle, GraphInvocationRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return new(invoke!(handle, request));
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<AgentGraphEvent> InvokeStreamAsync(
            AgentGraphHandle handle,
            GraphInvocationRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            foreach (var evt in graphEvents ?? [])
            {
                ct.ThrowIfCancellationRequested();
                yield return evt;
            }
        }
#pragma warning restore CS1998

        public ValueTask<GraphInvocationResult> ResumeAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<AgentGraphEvent> ResumeStreamAsync(AgentGraphHandle handle, GraphResumeRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask CancelAsync(AgentGraphHandle handle, string runId, CancellationToken ct = default) => default;
        public ValueTask EvictAsync(AgentGraphHandle handle, CancellationToken ct = default) => default;
    }

    private sealed class SyncFakeProvider(Func<CompletionRequest, CompletionResponse> respond) : ICompletionProvider
    {
        public string ProviderName => "SyncFake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(respond(request));
    }
}
