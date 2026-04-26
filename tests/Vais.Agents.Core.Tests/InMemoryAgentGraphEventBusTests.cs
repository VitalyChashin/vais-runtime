// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class InMemoryAgentGraphEventBusTests
{
    private static AgentGraphEvent MakeEvent() =>
        new GraphStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "run1", 0, "g", "1.0", "entry");

    [Fact]
    public async Task Publish_Fans_Out_To_All_Active_Subscribers()
    {
        var bus = new InMemoryAgentGraphEventBus();
        var a = new List<AgentGraphEvent>();
        var b = new List<AgentGraphEvent>();
        using var _1 = bus.Subscribe((e, _) => { a.Add(e); return ValueTask.CompletedTask; });
        using var _2 = bus.Subscribe((e, _) => { b.Add(e); return ValueTask.CompletedTask; });

        var evt = MakeEvent();
        await bus.PublishAsync(evt);

        a.Should().ContainSingle().Which.Should().BeSameAs(evt);
        b.Should().ContainSingle().Which.Should().BeSameAs(evt);
    }

    [Fact]
    public async Task Dispose_Unsubscribes_So_Future_Events_Bypass_That_Handler()
    {
        var bus = new InMemoryAgentGraphEventBus();
        var seen = new List<AgentGraphEvent>();
        var subscription = bus.Subscribe((e, _) => { seen.Add(e); return ValueTask.CompletedTask; });

        await bus.PublishAsync(MakeEvent());
        subscription.Dispose();
        await bus.PublishAsync(MakeEvent());

        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task A_Throwing_Subscriber_Does_Not_Break_Fan_Out()
    {
        var bus = new InMemoryAgentGraphEventBus();
        var seen = new List<AgentGraphEvent>();
        using var _1 = bus.Subscribe((_, _) => throw new InvalidOperationException("boom"));
        using var _2 = bus.Subscribe((e, _) => { seen.Add(e); return ValueTask.CompletedTask; });

        await bus.PublishAsync(MakeEvent());

        seen.Should().ContainSingle();
    }

    [Fact]
    public async Task Publish_With_No_Subscribers_Is_A_No_Op()
    {
        var bus = new InMemoryAgentGraphEventBus();
        await bus.PublishAsync(MakeEvent()); // must not throw
    }

    [Fact]
    public async Task Disposing_Twice_Is_Safe()
    {
        var bus = new InMemoryAgentGraphEventBus();
        var subscription = bus.Subscribe((_, _) => ValueTask.CompletedTask);
        subscription.Dispose();
        subscription.Dispose();

        var seen = 0;
        using var _ = bus.Subscribe((_, _) => { seen++; return ValueTask.CompletedTask; });
        await bus.PublishAsync(MakeEvent());
        seen.Should().Be(1);
    }
}
