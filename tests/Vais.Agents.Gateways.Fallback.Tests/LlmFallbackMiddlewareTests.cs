// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Gateways.Fallback.Tests;

/// <summary>
/// GW-11 — Tests for <see cref="LlmFallbackMiddleware"/>, <see cref="LlmLoadBalancingMiddleware"/>,
/// and <see cref="InMemoryFallbackProviderPool"/>.
/// </summary>
public sealed class LlmFallbackMiddlewareTests
{
    // ── Non-streaming fallback ────────────────────────────────────────────────

    [Fact]
    public async Task Fallback_Returns_First_Provider_On_Success()
    {
        var pool = new InMemoryFallbackProviderPool(
            new FixedProvider("primary"),
            new FixedProvider("secondary"));

        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmFallbackMiddleware(pool)],
        });

        var result = await agent.AskAsync("hello");
        result.Should().Be("primary");
    }

    [Fact]
    public async Task Fallback_Skips_Failing_Providers_And_Returns_Next()
    {
        var pool = new InMemoryFallbackProviderPool(
            new ThrowingProvider(),
            new FixedProvider("fallback-result"));

        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmFallbackMiddleware(pool)],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });

        var result = await agent.AskAsync("hello");
        result.Should().Be("fallback-result");
    }

    [Fact]
    public async Task Fallback_Throws_When_All_Providers_Fail()
    {
        var pool = new InMemoryFallbackProviderPool(
            new ThrowingProvider("err-1"),
            new ThrowingProvider("err-2"));

        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmFallbackMiddleware(pool)],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });

        Func<Task> act = () => agent.AskAsync("hello");
        await act.Should().ThrowAsync<Exception>();
    }

    // ── Streaming fallback ───────────────────────────────────────────────────

    [Fact]
    public async Task Streaming_Fallback_Returns_First_Provider_On_Success()
    {
        var pool = new InMemoryFallbackProviderPool(
            new StreamingFixedProvider("stream-primary"),
            new StreamingFixedProvider("stream-secondary"));

        var agent = new StatefulAiAgent(new StreamingStub(), new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmFallbackMiddleware(pool)],
        });

        var chunks = new List<string>();
        await foreach (var chunk in agent.StreamAsync("hello"))
            chunks.Add(chunk);

        string.Concat(chunks).Should().Contain("stream-primary");
    }

    [Fact]
    public async Task Streaming_Fallback_Skips_Failing_Provider()
    {
        var pool = new InMemoryFallbackProviderPool(
            new StreamingThrowingProvider(),
            new StreamingFixedProvider("stream-fallback"));

        var agent = new StatefulAiAgent(new StreamingStub(), new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmFallbackMiddleware(pool)],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });

        var chunks = new List<string>();
        await foreach (var chunk in agent.StreamAsync("hello"))
            chunks.Add(chunk);

        string.Concat(chunks).Should().Contain("stream-fallback");
    }

    // ── Load balancing ───────────────────────────────────────────────────────

    [Fact]
    public async Task LoadBalancing_Round_Robins_Across_Providers()
    {
        var called = new List<string>();
        var p1 = new CallTrackingProvider("p1", called);
        var p2 = new CallTrackingProvider("p2", called);
        var pool = new InMemoryFallbackProviderPool(p1, p2);

        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmLoadBalancingMiddleware(pool)],
        });

        await agent.AskAsync("q1");
        await agent.AskAsync("q2");
        await agent.AskAsync("q3");
        await agent.AskAsync("q4");

        called.Should().HaveCount(4);
        // Round-robin: p2,p1,p2,p1 or p1,p2,p1,p2 depending on start. Both providers must be hit.
        called.Should().Contain("p1").And.Contain("p2");
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class NeverReachedProvider : ICompletionProvider
    {
        public string ProviderName => "never";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
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

    private sealed class FixedProvider(string text) : ICompletionProvider
    {
        public string ProviderName => "fixed";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse(text));
    }

    private sealed class ThrowingProvider(string message = "boom") : ICompletionProvider
    {
        public string ProviderName => "throwing";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);
    }

    private sealed class CallTrackingProvider(string name, List<string> log) : ICompletionProvider
    {
        public string ProviderName => name;
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return Task.FromResult(new CompletionResponse("ok"));
        }
    }

    private sealed class StreamingFixedProvider(string text) : ICompletionProvider, IStreamingCompletionProvider
    {
        public string ProviderName => "streaming-fixed";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse(text));

#pragma warning disable CS1998
        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
            CompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new CompletionUpdate(text);
        }
#pragma warning restore CS1998
    }

    private sealed class StreamingThrowingProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        public string ProviderName => "streaming-throwing";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => ThrowAsync(cancellationToken);

#pragma warning disable CS0162 // unreachable yield break is intentional — iterator shape requires it
        private static async IAsyncEnumerable<CompletionUpdate> ThrowAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("streaming-boom");
            yield break;
        }
#pragma warning restore CS0162
    }
}
