// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class RunBudgetTests
{
    [Fact]
    public void Unlimited_Has_All_Nulls()
    {
        RunBudget.Unlimited.MaxTurns.Should().BeNull();
        RunBudget.Unlimited.MaxToolCalls.Should().BeNull();
        RunBudget.Unlimited.MaxPromptTokens.Should().BeNull();
        RunBudget.Unlimited.MaxCompletionTokens.Should().BeNull();
        RunBudget.Unlimited.MaxDuration.Should().BeNull();
    }

    [Fact]
    public void Record_Equality_Works_On_All_Fields()
    {
        var a = new RunBudget(MaxTurns: 5, MaxDuration: TimeSpan.FromMinutes(1));
        var b = new RunBudget(MaxTurns: 5, MaxDuration: TimeSpan.FromMinutes(1));
        a.Should().Be(b);
    }
}

public sealed class StreamingFilterTests
{
    [Fact]
    public async Task No_Filters_Yields_Unchanged_Deltas()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("hello "),
            new CompletionUpdate("world"),
        });
        var agent = new StatefulAiAgent(provider);

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi"))
        {
            deltas.Add(d);
        }

        deltas.Should().Equal("hello ", "world");
    }

    [Fact]
    public async Task Single_Filter_Transforms_Each_Delta()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("hello"),
            new CompletionUpdate("world"),
        });
        var filter = new DeltaTransformingFilter(u => new CompletionUpdate(u.TextDelta.ToUpperInvariant(), u.ModelId, u.PromptTokens, u.CompletionTokens));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new[] { filter },
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi"))
        {
            deltas.Add(d);
        }

        deltas.Should().Equal("HELLO", "WORLD");
    }

    [Fact]
    public async Task Multiple_Filters_Chain_In_Order()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("ab") });
        var upper = new DeltaTransformingFilter(u => new CompletionUpdate(u.TextDelta.ToUpperInvariant()));
        var exclaim = new DeltaTransformingFilter(u => new CompletionUpdate(u.TextDelta + "!"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new IStreamingAgentFilter[] { upper, exclaim },
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi"))
        {
            deltas.Add(d);
        }

        deltas.Should().ContainSingle().Which.Should().Be("AB!");
    }

    [Fact]
    public async Task OnStreamCompleteAsync_Fires_With_Accumulated_Response()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("part 1 ", ModelId: "m1", PromptTokens: 10),
            new CompletionUpdate("part 2", CompletionTokens: 5),
        });
        CompletionResponse? observed = null;
        var filter = new CompletingObserverFilter(r => observed = r);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new[] { filter },
        });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        observed.Should().NotBeNull();
        observed!.Text.Should().Be("part 1 part 2");
        observed.ModelId.Should().Be("m1");
        observed.PromptTokens.Should().Be(10);
        observed.CompletionTokens.Should().Be(5);
    }

    [Fact]
    public async Task Delta_Filter_Exception_Aborts_Stream_And_Fires_TurnFailed()
    {
        var bus = new Vais.Agents.Hosting.InMemory.InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("first"),
            new CompletionUpdate("second"),
        });
        var failing = new DeltaTransformingFilter(_ => throw new InvalidOperationException("delta-filter-boom"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            EventBus = bus,
            StreamingFilters = new[] { failing },
        });

        var deltas = new List<string>();
        Func<Task> act = async () =>
        {
            await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }
        };

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be("delta-filter-boom");
        deltas.Should().BeEmpty();
        events.Should().HaveCount(2);
        events[0].Should().BeOfType<TurnStarted>();
        events[1].Should().BeOfType<TurnFailed>();
        agent.Session.History.Should().ContainSingle().Which.Role.Should().Be(AgentChatRole.User);
    }

    [Fact]
    public async Task OnStreamComplete_Exception_Aborts_Turn_After_Deltas_Emitted()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("text"),
        });
        var filter = new CompletingObserverFilter(_ => throw new InvalidOperationException("complete-boom"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new[] { filter },
        });

        var deltas = new List<string>();
        Func<Task> act = async () =>
        {
            await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }
        };

        await act.Should().ThrowAsync<InvalidOperationException>();
        // Deltas emit normally — the OnStreamCompleteAsync hook fires AFTER drain.
        deltas.Should().Equal("text");
        // But assistant turn is NOT appended (turn failed after completion hook threw).
        agent.Session.History.Should().ContainSingle().Which.Role.Should().Be(AgentChatRole.User);
    }

    [Fact]
    public async Task Filter_Can_Change_Metadata_But_Text_Still_Accumulates_Original()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("A"),
            new CompletionUpdate("B"),
        });
        // Filter returns an update whose TextDelta is what's yielded; the accumulator
        // uses the post-filter TextDelta.
        var filter = new DeltaTransformingFilter(u => new CompletionUpdate(u.TextDelta.ToLowerInvariant()));

        CompletionResponse? observed = null;
        var observer = new CompletingObserverFilter(r => observed = r);

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new IStreamingAgentFilter[] { filter, observer },
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }

        deltas.Should().Equal("a", "b");
        observed!.Text.Should().Be("ab");
    }

    // ---- helpers ----

    private sealed class DeltaTransformingFilter(Func<CompletionUpdate, CompletionUpdate> map) : IStreamingAgentFilter
    {
        public ValueTask<CompletionUpdate> OnStreamDeltaAsync(CompletionUpdate update, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(map(update));
    }

    private sealed class CompletingObserverFilter(Action<CompletionResponse> observe) : IStreamingAgentFilter
    {
        public ValueTask OnStreamCompleteAsync(CompletionResponse final, CancellationToken cancellationToken = default)
        {
            observe(final);
            return ValueTask.CompletedTask;
        }
    }
}
