// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Verifies <see cref="StatefulAiAgent"/>'s <see cref="IAgentEventBus"/> integration:
/// AskAsync + StreamAsync publish TurnStarted before the provider call and
/// TurnCompleted / TurnFailed after the turn resolves, with AgentName merged from
/// options when the ambient context doesn't carry one.
/// </summary>
public sealed class StatefulAiAgentEventPublishingTests
{
    [Fact]
    public async Task AskAsync_Publishes_Started_Then_Completed_On_Happy_Path()
    {
        var bus = new InMemoryAgentEventBus();
        var events = Collect(bus);

        var provider = new FakeCompletionProvider(_ => new CompletionResponse(
            "hi there", ModelId: "test-model", PromptTokens: 7, CompletionTokens: 2));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            AgentName = "test-agent",
            EventBus = bus,
        });

        await agent.AskAsync("hello");

        events.Should().HaveCount(2);
        var started = events[0].Should().BeOfType<TurnStarted>().Which;
        started.UserMessage.Should().Be("hello");
        started.Context.AgentName.Should().Be("test-agent");

        var completed = events[1].Should().BeOfType<TurnCompleted>().Which;
        completed.AssistantText.Should().Be("hi there");
        completed.ModelId.Should().Be("test-model");
        completed.PromptTokens.Should().Be(7);
        completed.CompletionTokens.Should().Be(2);
        completed.Context.AgentName.Should().Be("test-agent");
    }

    [Fact]
    public async Task AskAsync_Publishes_Started_Then_Failed_When_Provider_Throws()
    {
        var bus = new InMemoryAgentEventBus();
        var events = Collect(bus);

        var provider = new FakeCompletionProvider(_ => throw new InvalidOperationException("boom"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            EventBus = bus,
            // Disable retries so the test doesn't wait for back-off.
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
        });

        Func<Task> act = () => agent.AskAsync("boom");
        await act.Should().ThrowAsync<InvalidOperationException>();

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<TurnStarted>();
        var failed = events[1].Should().BeOfType<TurnFailed>().Which;
        failed.ErrorType.Should().Be(nameof(InvalidOperationException));
        failed.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task StreamAsync_Publishes_Started_Then_Completed_With_Full_Accumulated_Text()
    {
        var bus = new InMemoryAgentEventBus();
        var events = Collect(bus);

        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("Hello"),
            new CompletionUpdate(", "),
            new CompletionUpdate("world", ModelId: "stream-model", PromptTokens: 4, CompletionTokens: 3),
        });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            AgentName = "stream-agent",
            EventBus = bus,
        });

        await foreach (var _ in agent.StreamAsync("greet")) { }

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<TurnStarted>();
        var completed = events[1].Should().BeOfType<TurnCompleted>().Which;
        completed.AssistantText.Should().Be("Hello, world");
        completed.ModelId.Should().Be("stream-model");
        completed.PromptTokens.Should().Be(4);
        completed.CompletionTokens.Should().Be(3);
    }

    [Fact]
    public async Task No_EventBus_Configured_Is_A_No_Op_Path_And_Does_Not_Throw()
    {
        // Sanity: the default path (no bus wired) must not impose any requirement on providers
        // nor throw when events would otherwise be published.
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var agent = new StatefulAiAgent(provider);

        var reply = await agent.AskAsync("hi");
        reply.Should().Be("ok");
    }

    [Fact]
    public async Task EventContext_Prefers_Ambient_AgentName_Over_Options_AgentName()
    {
        // Overlay rule: options AgentName is the *fallback* when ambient context has none.
        // If consumers set AgentName on the context accessor, don't clobber it.
        var bus = new InMemoryAgentEventBus();
        var events = Collect(bus);

        var accessor = new AsyncLocalAgentContextAccessor();
        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(_ => new CompletionResponse("ok")),
            new StatefulAgentOptions
            {
                AgentName = "options-name",
                EventBus = bus,
                ContextAccessor = accessor,
            });

        using (accessor.Push(new AgentContext(AgentName: "ambient-name")))
        {
            await agent.AskAsync("hi");
        }

        events.OfType<TurnStarted>().Single().Context.AgentName.Should().Be("ambient-name");
    }

    [Fact]
    public async Task AskAsync_ErrorInterceptor_RewritesMessage_KeepsTypeAndStillThrows()
    {
        var bus = new InMemoryAgentEventBus();
        var events = Collect(bus);

        var provider = new FakeCompletionProvider(_ => throw new InvalidOperationException("boom"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            AgentName = "err-agent",
            EventBus = bus,
            ResiliencePipeline = Polly.ResiliencePipeline.Empty,
            ErrorInterceptors = [new PrefixingErrorInterceptor("[support#42] ")],
        });

        Func<Task> act = () => agent.AskAsync("boom");
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("boom", "the original exception still propagates unchanged (P9)");

        var failed = events.OfType<TurnFailed>().Single();
        failed.ErrorType.Should().Be(nameof(InvalidOperationException), "ErrorType is immutable (P9)");
        failed.ErrorMessage.Should().Be("[support#42] boom", "the interceptor rewrites the surfaced message");
    }

    private sealed class PrefixingErrorInterceptor(string prefix) : ErrorInterceptor
    {
        public override Task<ErrorOutcome> OnErrorAsync(ErrorContext ctx, CancellationToken ct = default)
            => Task.FromResult(new ErrorOutcome(prefix + ctx.ErrorMessage));
    }

    private static List<AgentEvent> Collect(InMemoryAgentEventBus bus)
    {
        var list = new List<AgentEvent>();
        bus.Subscribe((e, _) => { list.Add(e); return ValueTask.CompletedTask; });
        return list;
    }
}
