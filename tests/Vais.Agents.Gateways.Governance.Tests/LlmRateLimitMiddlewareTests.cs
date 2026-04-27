// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Gateways.Testing;
using Xunit;

namespace Vais.Agents.Gateways.Governance.Tests;

/// <summary>
/// GW-13 — Tests for <see cref="LlmRateLimitMiddleware"/> and <see cref="InMemorySlidingWindowRateLimitStore"/>.
/// </summary>
public sealed class LlmRateLimitMiddlewareTests
{
    private static StatefulAiAgent BuildAgent(
        RateLimitOptions options,
        IRateLimitStore store,
        IAgentContextAccessor? accessor = null,
        params CompletionResponse[] responses)
    {
        accessor ??= new AsyncLocalAgentContextAccessor();
        return new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmRateLimitMiddleware(store, options, accessor),
                new LlmMockMiddleware(responses),
            ],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });
    }

    [Fact]
    public async Task First_Request_Within_Limit_Succeeds()
    {
        var store = new InMemorySlidingWindowRateLimitStore();
        var agent = BuildAgent(
            new RateLimitOptions { MaxRequestsPerWindow = 5, Window = TimeSpan.FromMinutes(1) },
            store,
            null,
            new CompletionResponse("ok"));

        var result = await agent.AskAsync("hello");

        result.Should().Be("ok");
    }

    [Fact]
    public async Task Exceeding_Request_Limit_Throws_BudgetExceededException()
    {
        var store = new InMemorySlidingWindowRateLimitStore();
        var options = new RateLimitOptions { MaxRequestsPerWindow = 2, Window = TimeSpan.FromMinutes(1) };
        var agent = BuildAgent(options, store, null,
            new CompletionResponse("r1"), new CompletionResponse("r2"), new CompletionResponse("r3"));

        await agent.AskAsync("q1");
        await agent.AskAsync("q2");

        Func<Task> act = () => agent.AskAsync("q3");
        await act.Should().ThrowAsync<AgentBudgetExceededException>()
            .Where(e => e.BudgetField == "RateLimitRequests");
    }

    [Fact]
    public async Task Window_Expiry_Resets_Counter()
    {
        var fakeTime = new FakeTimeProvider();
        var store = new InMemorySlidingWindowRateLimitStore(fakeTime);
        var options = new RateLimitOptions { MaxRequestsPerWindow = 1, Window = TimeSpan.FromMinutes(1) };
        var agent = BuildAgent(options, store, null,
            new CompletionResponse("first"), new CompletionResponse("second"));

        await agent.AskAsync("q1"); // uses the 1 allowed slot

        fakeTime.Advance(TimeSpan.FromMinutes(2)); // slide window past the first call

        var r2 = await agent.AskAsync("q2"); // window reset — should succeed
        r2.Should().Be("second");
    }

    [Fact]
    public async Task Token_Limit_Throws_When_Exceeded()
    {
        // Rough estimate: each char ≈ 0.25 tokens. "hello" = 5 chars ≈ 1 token.
        // With MaxTokensPerWindow=1, even a tiny message should exceed quickly.
        // Use a very large message to guarantee the limit is exceeded.
        var store = new InMemorySlidingWindowRateLimitStore();
        var options = new RateLimitOptions
        {
            MaxTokensPerWindow = 1,
            Window = TimeSpan.FromMinutes(1),
        };

        var longMessage = new string('x', 100); // 100 chars ≈ 25 tokens
        var agent = BuildAgent(options, store, null, new CompletionResponse("ok"));

        Func<Task> act = () => agent.AskAsync(longMessage);
        await act.Should().ThrowAsync<AgentBudgetExceededException>()
            .Where(e => e.BudgetField == "RateLimitTokens");
    }

    [Fact]
    public async Task Different_Keys_Have_Independent_Counters()
    {
        var store = new InMemorySlidingWindowRateLimitStore();
        var options = new RateLimitOptions { MaxRequestsPerWindow = 1, Window = TimeSpan.FromMinutes(1) };
        var accessor = new AsyncLocalAgentContextAccessor();

        var agent = new StatefulAiAgent(new NeverReachedProvider(), new StatefulAgentOptions
        {
            GatewayMiddleware =
            [
                new LlmRateLimitMiddleware(store, options, accessor),
                new LlmMockMiddleware(
                    new CompletionResponse("user-a-response"),
                    new CompletionResponse("user-b-response")),
            ],
            ContextAccessor = accessor,
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });

        using var _ = accessor.Push(new AgentContext { UserId = "user-a" });
        var r1 = await agent.AskAsync("q1");

        using var __ = accessor.Push(new AgentContext { UserId = "user-b" });
        var r2 = await agent.AskAsync("q2"); // different key → independent counter

        r1.Should().Be("user-a-response");
        r2.Should().Be("user-b-response");
    }

    private sealed class NeverReachedProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        public string ProviderName => "never";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Should not reach provider.");
    }
}
