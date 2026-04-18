// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class GuardrailOutcomeTests
{
    [Fact]
    public void Pass_Singleton_Has_Pass_Decision()
    {
        GuardrailOutcome.Pass.Decision.Should().Be(GuardrailDecision.Pass);
        GuardrailOutcome.Pass.Reason.Should().BeNull();
    }

    [Fact]
    public void Deny_Factory_Produces_Deny_With_Optional_Reason()
    {
        GuardrailOutcome.Deny().Decision.Should().Be(GuardrailDecision.Deny);
        GuardrailOutcome.Deny().Reason.Should().BeNull();
        GuardrailOutcome.Deny("prompt-injection").Reason.Should().Be("prompt-injection");
    }
}

public sealed class AgentGuardrailDeniedExceptionTests
{
    [Fact]
    public void Carries_Layer_And_Reason()
    {
        var ex = new AgentGuardrailDeniedException(GuardrailLayer.Input, "too many tokens");
        ex.Layer.Should().Be(GuardrailLayer.Input);
        ex.Reason.Should().Be("too many tokens");
        ex.Message.Should().Contain("Input").And.Contain("too many tokens");
    }

    [Fact]
    public void Message_Without_Reason_Is_Concise()
    {
        var ex = new AgentGuardrailDeniedException(GuardrailLayer.Output, reason: null);
        ex.Reason.Should().BeNull();
        ex.Message.Should().Contain("Output").And.NotContain(":");
    }
}

public sealed class StatefulAiAgentGuardrailIntegrationTests
{
    [Fact]
    public async Task Input_Guardrail_Pass_Lets_Turn_Proceed()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("reply"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            InputGuardrails = new[] { new FixedInputGuardrail(GuardrailOutcome.Pass) },
        });

        var reply = await agent.AskAsync("hi");
        reply.Should().Be("reply");
        agent.Session.History.Should().HaveCount(2);
    }

    [Fact]
    public async Task Input_Guardrail_Deny_Throws_And_Leaves_No_Assistant_Turn()
    {
        var providerCalled = false;
        var provider = new FakeCompletionProvider(_ => { providerCalled = true; return new CompletionResponse("should-not-run"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            InputGuardrails = new[] { new FixedInputGuardrail(GuardrailOutcome.Deny("blocked")) },
        });

        Func<Task> act = async () => await agent.AskAsync("hi");

        var thrown = await act.Should().ThrowAsync<AgentGuardrailDeniedException>();
        thrown.Which.Layer.Should().Be(GuardrailLayer.Input);
        thrown.Which.Reason.Should().Be("blocked");
        providerCalled.Should().BeFalse();
        agent.Session.History.Should().ContainSingle()  // user turn still in session; no assistant turn
            .Which.Role.Should().Be(AgentChatRole.User);
    }

    [Fact]
    public async Task Output_Guardrail_Pass_Appends_Assistant()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            OutputGuardrails = new[] { new FixedOutputGuardrail(GuardrailOutcome.Pass) },
        });

        await agent.AskAsync("hi");

        agent.Session.History.Should().HaveCount(2);
        agent.Session.History[1].Should().Be(new ChatTurn(AgentChatRole.Assistant, "ok"));
    }

    [Fact]
    public async Task Output_Guardrail_Deny_Throws_And_Does_Not_Append_Assistant()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("forbidden-content"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            OutputGuardrails = new[] { new FixedOutputGuardrail(GuardrailOutcome.Deny("content-policy")) },
        });

        Func<Task> act = async () => await agent.AskAsync("hi");

        var thrown = await act.Should().ThrowAsync<AgentGuardrailDeniedException>();
        thrown.Which.Layer.Should().Be(GuardrailLayer.Output);
        thrown.Which.Reason.Should().Be("content-policy");
        agent.Session.History.Should().ContainSingle().Which.Role.Should().Be(AgentChatRole.User);
    }

    [Fact]
    public async Task First_Denying_Guardrail_Short_Circuits_Rest_Of_Chain()
    {
        var secondInvoked = false;
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("never"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            InputGuardrails = new IInputGuardrail[]
            {
                new FixedInputGuardrail(GuardrailOutcome.Deny("first-says-no")),
                new CountingInputGuardrail(() => secondInvoked = true),
            },
        });

        Func<Task> act = async () => await agent.AskAsync("hi");
        await act.Should().ThrowAsync<AgentGuardrailDeniedException>();

        secondInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task Denial_Emits_TurnFailed_On_Event_Bus()
    {
        var bus = new Vais2.Agents.Hosting.InMemory.InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(_ => new CompletionResponse("ok")),
            new StatefulAgentOptions
            {
                EventBus = bus,
                InputGuardrails = new[] { new FixedInputGuardrail(GuardrailOutcome.Deny("reason-text")) },
            });

        Func<Task> act = async () => await agent.AskAsync("hi");
        await act.Should().ThrowAsync<AgentGuardrailDeniedException>();

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<TurnStarted>();
        events[1].Should().BeOfType<GuardrailTriggered>()
            .Which.Should().Match<GuardrailTriggered>(g =>
                g.Layer == GuardrailLayer.Input &&
                g.Decision == GuardrailDecision.Deny &&
                g.Reason == "reason-text");
        events[2].Should().BeOfType<TurnFailed>()
            .Which.ErrorType.Should().Be(nameof(AgentGuardrailDeniedException));
    }

    [Fact]
    public async Task Streaming_Input_Guardrail_Deny_Throws_Without_Emitting_Deltas()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("chunk1"),
            new CompletionUpdate("chunk2"),
        });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            InputGuardrails = new[] { new FixedInputGuardrail(GuardrailOutcome.Deny("no-streaming")) },
        });

        var deltas = new List<string>();
        Func<Task> act = async () =>
        {
            await foreach (var delta in agent.StreamAsync("hi"))
            {
                deltas.Add(delta);
            }
        };

        var thrown = await act.Should().ThrowAsync<AgentGuardrailDeniedException>();
        thrown.Which.Layer.Should().Be(GuardrailLayer.Input);
        deltas.Should().BeEmpty();
        agent.Session.History.Should().ContainSingle().Which.Role.Should().Be(AgentChatRole.User);
    }

    [Fact]
    public async Task Streaming_Output_Guardrail_Deny_Runs_Post_Facto_And_Skips_Assistant_Append()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("full "),
            new CompletionUpdate("reply"),
        });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            OutputGuardrails = new[] { new FixedOutputGuardrail(GuardrailOutcome.Deny("blocked-post")) },
        });

        var deltas = new List<string>();
        Func<Task> act = async () =>
        {
            await foreach (var delta in agent.StreamAsync("hi"))
            {
                deltas.Add(delta);
            }
        };

        await act.Should().ThrowAsync<AgentGuardrailDeniedException>();

        // Deltas still went out (post-facto denial — documented).
        deltas.Should().Equal("full ", "reply");
        // But assistant turn was NOT appended.
        agent.Session.History.Should().ContainSingle().Which.Role.Should().Be(AgentChatRole.User);
    }

    // ---- helpers ----

    private sealed class FixedInputGuardrail(GuardrailOutcome outcome) : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(
            CompletionRequest request,
            AgentContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(outcome);
    }

    private sealed class FixedOutputGuardrail(GuardrailOutcome outcome) : IOutputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(
            CompletionResponse response,
            AgentContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(outcome);
    }

    private sealed class CountingInputGuardrail(Action onInvoke) : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(
            CompletionRequest request,
            AgentContext context,
            CancellationToken cancellationToken = default)
        {
            onInvoke();
            return ValueTask.FromResult(GuardrailOutcome.Pass);
        }
    }
}
