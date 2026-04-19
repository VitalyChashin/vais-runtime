// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// Round-trip tests for <see cref="OrleansAgentEventBus"/>. The bus is
/// provider-neutral; here we exercise it against Orleans' in-process
/// <c>AddMemoryStreams</c>, which is enough to verify the wiring end-to-end:
/// surrogate serialisation for all three <see cref="AgentEvent"/> subclasses
/// plus the <see cref="Subscribe"/> observer adapter.
/// </summary>
/// <remarks>
/// Durable cross-silo providers (<c>Microsoft.Orleans.Streaming.AzureEventHubs</c>
/// 9.x, a future <c>Microsoft.Orleans.Streaming.Redis</c> stable, etc.) plug in
/// through the same <see cref="OrleansAgentEventBus"/> by name. We don't spin up
/// an external broker here — the goal of this test is "the surrogate + the
/// observer adapter + the IDisposable dance all work"; provider-specific
/// behaviour is out of scope for M3e-3b.
/// </remarks>
[Collection(OrleansStreamsCollection.CollectionName)]
public sealed class OrleansAgentEventBusTests
{
    private readonly OrleansStreamsFixture _fx;

    public OrleansAgentEventBusTests(OrleansStreamsFixture fx) => _fx = fx;

    [Fact]
    public async Task Publishes_And_Subscribes_TurnStarted_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        var received = new List<AgentEvent>();
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            received.Add(e);
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });

        var @event = new TurnStarted(
            DateTimeOffset.UtcNow,
            new AgentContext(UserId: "u", TenantId: "t", AgentName: "a"),
            "hello");
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.Should().ContainSingle().Which.Should().BeOfType<TurnStarted>().Which.UserMessage.Should().Be("hello");
        received[0].Context.AgentName.Should().Be("a");
    }

    [Fact]
    public async Task Publishes_And_Subscribes_TurnCompleted_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        TurnCompleted? observed = null;
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            if (e is TurnCompleted completed)
            {
                observed = completed;
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });

        var @event = new TurnCompleted(
            DateTimeOffset.UtcNow,
            AgentContext.Empty,
            AssistantText: "done",
            ModelId: "gpt-test",
            PromptTokens: 42,
            CompletionTokens: 9,
            Duration: TimeSpan.FromMilliseconds(123));
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.AssistantText.Should().Be("done");
        observed.ModelId.Should().Be("gpt-test");
        observed.PromptTokens.Should().Be(42);
        observed.CompletionTokens.Should().Be(9);
        observed.Duration.Should().Be(TimeSpan.FromMilliseconds(123));
    }

    [Fact]
    public async Task Publishes_And_Subscribes_TurnFailed_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        TurnFailed? observed = null;
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            if (e is TurnFailed failed)
            {
                observed = failed;
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });

        var @event = new TurnFailed(
            DateTimeOffset.UtcNow,
            AgentContext.Empty,
            ErrorType: "InvalidOperationException",
            ErrorMessage: "boom",
            Duration: TimeSpan.FromMilliseconds(7));
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.ErrorType.Should().Be("InvalidOperationException");
        observed.ErrorMessage.Should().Be("boom");
        observed.Duration.Should().Be(TimeSpan.FromMilliseconds(7));
    }

    [Fact]
    public async Task Publishes_And_Subscribes_ToolCallStarted_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        ToolCallStarted? observed = null;
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            if (e is ToolCallStarted started)
            {
                observed = started;
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });

        var @event = new ToolCallStarted(
            DateTimeOffset.UtcNow,
            new AgentContext(UserId: "u"),
            CallId: "call-42",
            ToolName: "get_weather");
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.CallId.Should().Be("call-42");
        observed.ToolName.Should().Be("get_weather");
        observed.Context.UserId.Should().Be("u");
    }

    [Fact]
    public async Task Publishes_And_Subscribes_ToolCallCompleted_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        ToolCallCompleted? observed = null;
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            if (e is ToolCallCompleted completed)
            {
                observed = completed;
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });

        var @event = new ToolCallCompleted(
            DateTimeOffset.UtcNow,
            AgentContext.Empty,
            CallId: "call-7",
            ToolName: "search",
            Succeeded: false,
            Error: "InvalidOperationException",
            Duration: TimeSpan.FromMilliseconds(55));
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.CallId.Should().Be("call-7");
        observed.ToolName.Should().Be("search");
        observed.Succeeded.Should().BeFalse();
        observed.Error.Should().Be("InvalidOperationException");
        observed.Duration.Should().Be(TimeSpan.FromMilliseconds(55));
    }

    [Fact]
    public async Task Publishes_And_Subscribes_GuardrailTriggered_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        GuardrailTriggered? observed = null;
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            if (e is GuardrailTriggered triggered)
            {
                observed = triggered;
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });

        var @event = new GuardrailTriggered(
            DateTimeOffset.UtcNow,
            AgentContext.Empty,
            Layer: GuardrailLayer.Output,
            Decision: GuardrailDecision.Deny,
            Reason: "content-policy");
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.Layer.Should().Be(GuardrailLayer.Output);
        observed.Decision.Should().Be(GuardrailDecision.Deny);
        observed.Reason.Should().Be("content-policy");
    }

    [Fact]
    public async Task Publishes_And_Subscribes_InterruptRaised_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        InterruptRaised? observed = null;
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            if (e is InterruptRaised raised)
            {
                observed = raised;
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });

        var @event = new InterruptRaised(
            DateTimeOffset.UtcNow,
            AgentContext.Empty,
            InterruptId: "intr-42",
            Reason: "approval required");
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.InterruptId.Should().Be("intr-42");
        observed.Reason.Should().Be("approval required");
    }

    [Fact]
    public async Task Publishes_And_Subscribes_HandoffRequested_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        HandoffRequested? observed = null;
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            if (e is HandoffRequested requested)
            {
                observed = requested;
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });

        var @event = new HandoffRequested(
            DateTimeOffset.UtcNow,
            AgentContext.Empty,
            new Handoff("alice", "bob", "escalating to legal"));
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.Handoff.FromAgent.Should().Be("alice");
        observed.Handoff.ToAgent.Should().Be("bob");
        observed.Handoff.Message.Should().Be("escalating to legal");
    }

    [Fact]
    public async Task Publishes_And_Subscribes_ToolCallReplayed_Round_Trip()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);

        ToolCallReplayed? observed = null;
        var tcs = new TaskCompletionSource();
        using var subscription = bus.Subscribe((e, _) =>
        {
            if (e is ToolCallReplayed replayed)
            {
                observed = replayed;
                tcs.TrySetResult();
            }
            return ValueTask.CompletedTask;
        });

        var @event = new ToolCallReplayed(
            DateTimeOffset.UtcNow,
            AgentContext.Empty with { RunId = "run-42" },
            "call-1",
            "weather");
        await bus.PublishAsync(@event);

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Should().NotBeNull();
        observed!.CallId.Should().Be("call-1");
        observed.ToolName.Should().Be("weather");
        observed.Context.RunId.Should().Be("run-42");
    }

    [Fact]
    public async Task Dispose_Unsubscribes_And_Later_Events_Are_Not_Received()
    {
        var bus = new OrleansAgentEventBus(_fx.Cluster.Client, OrleansAgentEventBus.StreamNamespace);
        var firstReceived = new TaskCompletionSource();
        var count = 0;
        var subscription = bus.Subscribe((_, _) =>
        {
            count++;
            firstReceived.TrySetResult();
            return ValueTask.CompletedTask;
        });

        await bus.PublishAsync(new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "first"));
        await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        subscription.Dispose();
        await Task.Delay(100); // give Orleans a beat to unwire

        await bus.PublishAsync(new TurnStarted(DateTimeOffset.UtcNow, AgentContext.Empty, "second"));
        await Task.Delay(300); // wait long enough to be confident a second delivery would have landed

        count.Should().Be(1);
    }
}

/// <summary>
/// Fixture: Orleans <see cref="TestCluster"/> configured with in-process memory
/// streams under the conventional <see cref="OrleansAgentEventBus.StreamNamespace"/>
/// provider name, plus the <c>PubSubStore</c> memory grain storage that Orleans
/// streams require.
/// </summary>
public sealed class OrleansStreamsFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        builder.AddClientBuilderConfigurator<ClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        if (Cluster is not null)
        {
            await Cluster.StopAllSilosAsync();
            await Cluster.DisposeAsync();
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryStreams(OrleansAgentEventBus.StreamNamespace);
            siloBuilder.AddMemoryGrainStorage("PubSubStore");
        }
    }

    private sealed class ClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(Microsoft.Extensions.Configuration.IConfiguration configuration, IClientBuilder clientBuilder)
        {
            clientBuilder.AddMemoryStreams(OrleansAgentEventBus.StreamNamespace);
        }
    }
}

[CollectionDefinition(CollectionName)]
public sealed class OrleansStreamsCollection : ICollectionFixture<OrleansStreamsFixture>
{
    public const string CollectionName = "Orleans streams cluster";
}
