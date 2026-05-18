// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// CS-11: <see cref="RunCompletionEventBusBridge"/> fans out exactly TurnCompleted and
/// GraphCompleted to registered listeners; other event types are silently ignored.
/// </summary>
public sealed class RunCompletionEventBusBridgeTests
{
    private static AgentContext MakeCtx(string? runId = null) =>
        new(UserId: "u", TenantId: "t", AgentName: "agent-1")
        {
            RunId = runId ?? Guid.NewGuid().ToString(),
            WorkspaceId = "ws-1",
        };

    [Fact]
    public async Task TurnCompleted_FansOutToListener()
    {
        var bus = new InMemoryAgentEventBus();
        var signals = new List<RunCompletionSignal>();
        var listener = new CaptureListener(signals);

        var services = new ServiceCollection();
        services.AddSingleton<IRunCompletionListener>(listener);
        var sp = services.BuildServiceProvider();

        using var bridge = new RunCompletionEventBusBridge(bus, sp, NullLogger<RunCompletionEventBusBridge>.Instance);
        await bridge.StartAsync(default);

        var evt = new TurnCompleted(DateTimeOffset.UtcNow, MakeCtx(), "Hello", null, null, null, TimeSpan.FromMilliseconds(50));
        await bus.PublishAsync(evt);

        signals.Should().ContainSingle();
        signals[0].AgentRef.Should().Be("agent-1");
        signals[0].AssistantText.Should().Be("Hello");
        signals[0].WorkspaceId.Should().Be("ws-1");
    }

    [Fact]
    public async Task GraphCompleted_FansOutToListener_ViaGraphBus()
    {
        var bus = new InMemoryAgentEventBus();
        var graphBus = new InMemoryAgentGraphEventBus();
        var signals = new List<RunCompletionSignal>();
        var listener = new CaptureListener(signals);

        var services = new ServiceCollection();
        services.AddSingleton<IRunCompletionListener>(listener);
        var sp = services.BuildServiceProvider();

        using var bridge = new RunCompletionEventBusBridge(bus, sp, NullLogger<RunCompletionEventBusBridge>.Instance, graphBus);
        await bridge.StartAsync(default);

        var ctx = MakeCtx("run-99");
        var evt = new GraphCompleted(DateTimeOffset.UtcNow, ctx, "run-99", 3, "end", TimeSpan.FromSeconds(2));
        await graphBus.PublishAsync(evt);

        signals.Should().ContainSingle();
        signals[0].GraphRef.Should().Be("agent-1");
        signals[0].AgentRef.Should().BeNull();
        signals[0].AgentRunId.Should().Be("run-99");
    }

    [Fact]
    public async Task TurnStarted_IsIgnored_NoFanOut()
    {
        var bus = new InMemoryAgentEventBus();
        var signals = new List<RunCompletionSignal>();

        var services = new ServiceCollection();
        services.AddSingleton<IRunCompletionListener>(new CaptureListener(signals));
        var sp = services.BuildServiceProvider();

        using var bridge = new RunCompletionEventBusBridge(bus, sp, NullLogger<RunCompletionEventBusBridge>.Instance);
        await bridge.StartAsync(default);

        await bus.PublishAsync(new TurnStarted(DateTimeOffset.UtcNow, MakeCtx(), "hi"));

        signals.Should().BeEmpty();
    }

    [Fact]
    public async Task StopAsync_DisposesSubscription_NoMoreFanOut()
    {
        var bus = new InMemoryAgentEventBus();
        var signals = new List<RunCompletionSignal>();

        var services = new ServiceCollection();
        services.AddSingleton<IRunCompletionListener>(new CaptureListener(signals));
        var sp = services.BuildServiceProvider();

        var bridge = new RunCompletionEventBusBridge(bus, sp, NullLogger<RunCompletionEventBusBridge>.Instance);
        await bridge.StartAsync(default);
        await bridge.StopAsync(default);
        bridge.Dispose();

        var evt = new TurnCompleted(DateTimeOffset.UtcNow, MakeCtx(), "text", null, null, null, TimeSpan.Zero);
        await bus.PublishAsync(evt);

        signals.Should().BeEmpty();
    }

    private sealed class CaptureListener(List<RunCompletionSignal> sink) : IRunCompletionListener
    {
        public ValueTask OnRunCompletedAsync(RunCompletionSignal signal, CancellationToken ct)
        {
            sink.Add(signal);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryAgentEventBus : IAgentEventBus
    {
        private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _handlers = new();

        public async ValueTask PublishAsync(AgentEvent @event, CancellationToken ct = default)
        {
            foreach (var h in _handlers.ToList())
                await h(@event, ct);
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
        {
            _handlers.Add(handler);
            return new Reg(() => _handlers.Remove(handler));
        }

        private sealed class Reg(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }

    private sealed class InMemoryAgentGraphEventBus : IAgentGraphEventBus
    {
        private readonly List<Func<AgentGraphEvent, CancellationToken, ValueTask>> _handlers = new();

        public async ValueTask PublishAsync(AgentGraphEvent @event, CancellationToken ct = default)
        {
            foreach (var h in _handlers.ToList())
                await h(@event, ct);
        }

        public IDisposable Subscribe(Func<AgentGraphEvent, CancellationToken, ValueTask> handler)
        {
            _handlers.Add(handler);
            return new Reg(() => _handlers.Remove(handler));
        }

        private sealed class Reg(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }
}
