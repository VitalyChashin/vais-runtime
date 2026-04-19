// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class InMemoryAgentEventBusTests
{
    [Fact]
    public async Task Publish_Fans_Out_To_All_Active_Subscribers()
    {
        var bus = new InMemoryAgentEventBus();
        var a = new List<AgentEvent>();
        var b = new List<AgentEvent>();
        using var _1 = bus.Subscribe((e, _) => { a.Add(e); return ValueTask.CompletedTask; });
        using var _2 = bus.Subscribe((e, _) => { b.Add(e); return ValueTask.CompletedTask; });

        var @event = new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "hi");
        await bus.PublishAsync(@event);

        a.Should().ContainSingle().Which.Should().BeSameAs(@event);
        b.Should().ContainSingle().Which.Should().BeSameAs(@event);
    }

    [Fact]
    public async Task Dispose_Unsubscribes_So_Future_Events_Bypass_That_Handler()
    {
        var bus = new InMemoryAgentEventBus();
        var seen = new List<AgentEvent>();
        var subscription = bus.Subscribe((e, _) => { seen.Add(e); return ValueTask.CompletedTask; });

        await bus.PublishAsync(new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "first"));
        subscription.Dispose();
        await bus.PublishAsync(new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "second"));

        seen.Should().ContainSingle().Which.Should().BeOfType<TurnStarted>()
            .Which.UserMessage.Should().Be("first");
    }

    [Fact]
    public async Task A_Throwing_Subscriber_Does_Not_Break_Fan_Out()
    {
        var bus = new InMemoryAgentEventBus();
        var seen = new List<string>();
        using var _1 = bus.Subscribe((_, _) => throw new InvalidOperationException("boom"));
        using var _2 = bus.Subscribe((e, _) => { seen.Add(((TurnStarted)e).UserMessage); return ValueTask.CompletedTask; });

        await bus.PublishAsync(new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "survived"));

        seen.Should().ContainSingle().Which.Should().Be("survived");
    }

    [Fact]
    public async Task Publish_With_No_Subscribers_Is_A_No_Op()
    {
        var bus = new InMemoryAgentEventBus();
        // Should not throw or hang even before anyone has subscribed.
        await bus.PublishAsync(new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "none"));
    }

    [Fact]
    public async Task Disposing_Twice_Is_Safe()
    {
        var bus = new InMemoryAgentEventBus();
        var subscription = bus.Subscribe((_, _) => ValueTask.CompletedTask);
        subscription.Dispose();
        subscription.Dispose(); // must not throw / must not try to remove again

        // Subscribe a new handler and publish to confirm internal state is still clean.
        var seen = 0;
        using var _ = bus.Subscribe((_, _) => { seen++; return ValueTask.CompletedTask; });
        await bus.PublishAsync(new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "x"));
        seen.Should().Be(1);
    }
}
