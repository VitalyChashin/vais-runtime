// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Gateways.Testing;
using Xunit;

namespace Vais.Agents.Gateways.SemanticCache.Tests;

/// <summary>
/// GW-12 — Tests for <see cref="LlmSemanticCacheMiddleware"/> and <see cref="InMemorySemanticCacheStore"/>.
/// </summary>
public sealed class LlmSemanticCacheMiddlewareTests
{
    private static (StatefulAiAgent agent, InMemorySemanticCacheStore store, CallCountingProvider provider)
        BuildAgent(params CompletionResponse[] responses)
    {
        var store = new InMemorySemanticCacheStore();
        var provider = new CallCountingProvider(responses);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            // Cache wraps provider: index 0 (outermost) = cache, index 1 = mock
            GatewayMiddleware =
            [
                new LlmSemanticCacheMiddleware(store),
                new LlmMockMiddleware(responses),
            ],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });
        return (agent, store, provider);
    }

    [Fact]
    public async Task Cache_Miss_Calls_Provider_And_Stores_Response()
    {
        var store = new InMemorySemanticCacheStore();
        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmSemanticCacheMiddleware(store),
                new LlmMockMiddleware(new CompletionResponse("cached-result", ModelId: "m1")),
            ],
        });

        var result = await agent.AskAsync("hello");

        result.Should().Be("cached-result");
        var stored = await store.GetAsync("hello");
        stored.Should().NotBeNull();
        stored!.Text.Should().Be("cached-result");
    }

    [Fact]
    public async Task Cache_Hit_Returns_Stored_Response_Without_Provider_Call()
    {
        var store = new InMemorySemanticCacheStore();
        await store.SetAsync("hello", new CompletionResponse("from-cache"));

        // Mock has no queued responses — would throw if called.
        var mock = new LlmMockMiddleware();
        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmSemanticCacheMiddleware(store),
                mock,
            ],
        });

        var result = await agent.AskAsync("hello");

        result.Should().Be("from-cache");
    }

    [Fact]
    public async Task Second_Identical_Request_Hits_Cache()
    {
        var store = new InMemorySemanticCacheStore();
        var callCount = 0;
        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmSemanticCacheMiddleware(store),
                new CountingMockMiddleware(() =>
                {
                    callCount++;
                    return new CompletionResponse("answer");
                }),
            ],
        });

        await agent.AskAsync("what is 2+2");
        await agent.AskAsync("what is 2+2");

        callCount.Should().Be(1); // second call served from cache
    }

    [Fact]
    public async Task Different_Prompts_Are_Cached_Separately()
    {
        var store = new InMemorySemanticCacheStore();
        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmSemanticCacheMiddleware(store),
                new LlmMockMiddleware(
                    new CompletionResponse("answer-a"),
                    new CompletionResponse("answer-b")),
            ],
        });

        var r1 = await agent.AskAsync("question-a");
        var r2 = await agent.AskAsync("question-b");

        r1.Should().Be("answer-a");
        r2.Should().Be("answer-b");
    }

    [Fact]
    public async Task Streaming_Cache_Miss_Streams_And_Stores()
    {
        var store = new InMemorySemanticCacheStore();
        var stub = new StreamingStub();
        var agent = new StatefulAiAgent(stub, new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmSemanticCacheMiddleware(store),
                new LlmMockMiddleware(new CompletionResponse("streamed-text")),
            ],
        });

        var chunks = new List<string>();
        await foreach (var chunk in agent.StreamAsync("stream-question"))
            chunks.Add(chunk);

        string.Concat(chunks).Should().Contain("streamed-text");
        var stored = await store.GetAsync("stream-question");
        stored.Should().NotBeNull();
    }

    private sealed class NeverReachedProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        public string ProviderName => "never";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
    }

    private sealed class StreamingStub : ICompletionProvider, IStreamingCompletionProvider
    {
        public string ProviderName => "streaming-stub";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
    }

    private sealed class CallCountingProvider : ICompletionProvider
    {
        private readonly Queue<CompletionResponse> _responses;
        public int CallCount { get; private set; }
        public string ProviderName => "counting";
        public CallCountingProvider(CompletionResponse[] responses) => _responses = new Queue<CompletionResponse>(responses);
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class CountingMockMiddleware : LlmGatewayMiddleware
    {
        private readonly Func<CompletionResponse> _factory;
        public CountingMockMiddleware(Func<CompletionResponse> factory) => _factory = factory;

        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
            => Task.FromResult(_factory());
    }
}
