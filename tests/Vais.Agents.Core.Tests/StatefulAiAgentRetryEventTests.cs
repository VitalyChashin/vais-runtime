// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Part 1 (honest signals): the non-streaming retry path publishes an <see cref="LlmCallRetried"/>
/// event per failed attempt (WARNING level) so a recovered retry loop is observable rather than
/// silent. Parity with the streaming path's per-attempt <c>stream_attempt</c> spans.
/// </summary>
public sealed class StatefulAiAgentRetryEventTests
{
    [Fact]
    public async Task NonStreaming_Retry_Then_Success_Emits_LlmCallRetried_Per_Attempt()
    {
        var bus = new CapturingEventBus();
        // Default pipeline = 2 retries (3 attempts). Fail twice, then succeed.
        var provider = new FlakyProvider(failTimes: 2, finalText: "recovered");
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { EventBus = bus });

        var result = await agent.AskAsync("hello");

        result.Should().Be("recovered");
        var retries = bus.Events.OfType<LlmCallRetried>().ToList();
        retries.Should().HaveCount(2);
        retries[0].AttemptIndex.Should().Be(0);
        retries[1].AttemptIndex.Should().Be(1);
        retries.Should().OnlyContain(e => e.Level == FailureLevel.Warning);
        retries.Should().OnlyContain(e => e.ErrorType == nameof(InvalidOperationException));
        // No TurnFailed — the turn recovered.
        bus.Events.OfType<TurnFailed>().Should().BeEmpty();
    }

    [Fact]
    public async Task NonStreaming_Clean_Call_Emits_No_LlmCallRetried()
    {
        var bus = new CapturingEventBus();
        var provider = new FlakyProvider(failTimes: 0, finalText: "ok");
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { EventBus = bus });

        await agent.AskAsync("hello");

        bus.Events.OfType<LlmCallRetried>().Should().BeEmpty();
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class CapturingEventBus : IAgentEventBus
    {
        public List<AgentEvent> Events { get; } = [];
        public ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return ValueTask.CompletedTask;
        }
        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class FlakyProvider(int failTimes, string finalText) : ICompletionProvider
    {
        private int _calls;
        public string ProviderName => "flaky";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            if (_calls++ < failTimes)
            {
                throw new InvalidOperationException($"transient-fail-{_calls}");
            }
            return Task.FromResult(new CompletionResponse(finalText));
        }
    }
}
