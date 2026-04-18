// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais2.Agents.Hosting.InMemory;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class AgentInterruptedExceptionTests
{
    [Fact]
    public void Carries_The_Interrupt_Payload()
    {
        var interrupt = new AgentInterrupt("intr-1", "approval needed", JsonDocument.Parse("""{"amount":500}""").RootElement);
        var ex = new AgentInterruptedException(interrupt);

        ex.Interrupt.Should().BeSameAs(interrupt);
        ex.Message.Should().Contain("approval needed");
    }

    [Fact]
    public void Ctor_Rejects_Null_Interrupt()
    {
        Action act = () => _ = new AgentInterruptedException(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

public sealed class GuardrailOutcomeInterruptFactoryTests
{
    [Fact]
    public void Interrupt_Factory_Sets_Decision_Payload_And_Inherits_Reason()
    {
        var interrupt = new AgentInterrupt("intr-1", "from-interrupt", JsonDocument.Parse("{}").RootElement);
        var outcome = GuardrailOutcome.Interrupt(interrupt);

        outcome.Decision.Should().Be(GuardrailDecision.Interrupt);
        outcome.Reason.Should().Be("from-interrupt");
        outcome.InterruptPayload.Should().BeSameAs(interrupt);
    }

    [Fact]
    public void Interrupt_Factory_Reason_Override_Wins()
    {
        var interrupt = new AgentInterrupt("intr-1", "from-interrupt", JsonDocument.Parse("{}").RootElement);
        var outcome = GuardrailOutcome.Interrupt(interrupt, "caller-reason");

        outcome.Reason.Should().Be("caller-reason");
    }

    [Fact]
    public void Interrupt_Factory_Rejects_Null()
    {
        Action act = () => GuardrailOutcome.Interrupt(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

public sealed class StatefulAiAgentInterruptIntegrationTests
{
    [Fact]
    public async Task Input_Guardrail_Interrupt_Throws_AgentInterruptedException_With_Payload()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("never-reached"));
        var interrupt = new AgentInterrupt("i-1", "needs review", JsonDocument.Parse("""{"tool":"send_email"}""").RootElement);
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            InputGuardrails = new[] { new InterruptingInputGuardrail(interrupt) },
        });

        Func<Task> act = async () => await agent.AskAsync("hi");

        var thrown = await act.Should().ThrowAsync<AgentInterruptedException>();
        thrown.Which.Interrupt.InterruptId.Should().Be("i-1");
        thrown.Which.Interrupt.Reason.Should().Be("needs review");
    }

    [Fact]
    public async Task Interrupt_Emits_InterruptRaised_Event_Then_TurnFailed()
    {
        var bus = new InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var interrupt = new AgentInterrupt("i-2", "pause", JsonDocument.Parse("{}").RootElement);
        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(_ => new CompletionResponse("ok")),
            new StatefulAgentOptions
            {
                EventBus = bus,
                OutputGuardrails = new[] { new InterruptingOutputGuardrail(interrupt) },
            });

        Func<Task> act = async () => await agent.AskAsync("hi");
        await act.Should().ThrowAsync<AgentInterruptedException>();

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<TurnStarted>();
        events[1].Should().BeOfType<InterruptRaised>()
            .Which.Should().Match<InterruptRaised>(e => e.InterruptId == "i-2" && e.Reason == "pause");
        events[2].Should().BeOfType<TurnFailed>()
            .Which.ErrorType.Should().Be(nameof(AgentInterruptedException));
    }

    [Fact]
    public async Task ResumeAsync_Rejects_Null_Input()
    {
        var agent = new StatefulAiAgent(new FakeCompletionProvider());
        Func<Task> act = async () => await agent.ResumeAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ResumeAsync_Forwards_Payload_As_Next_User_Turn()
    {
        CompletionRequest? captured = null;
        var provider = new FakeCompletionProvider(req => { captured = req; return new CompletionResponse("post-resume-reply"); });
        var agent = new StatefulAiAgent(provider);

        var resumeInput = new ResumeInput("i-1", JsonDocument.Parse(""""
            "user said yes"
        """").RootElement);
        var reply = await agent.ResumeAsync(resumeInput);

        reply.Should().Be("post-resume-reply");
        captured!.History.Should().ContainSingle()
            .Which.Should().Match<ChatTurn>(t => t.Role == AgentChatRole.User && t.Text == "user said yes");
    }

    [Fact]
    public async Task Interrupt_Without_Payload_Throws_InvalidOperationException()
    {
        // Consumers constructing GuardrailOutcome(Decision.Interrupt) directly without
        // going through the factory must supply a payload; the runner catches the
        // misuse and throws a descriptive error instead of a null-reference.
        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(_ => new CompletionResponse("ok")),
            new StatefulAgentOptions
            {
                InputGuardrails = new[] { new BrokenInterruptGuardrail() },
            });

        Func<Task> act = async () => await agent.AskAsync("hi");
        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Interrupt without an AgentInterrupt payload");
    }

    // ---- helpers ----

    private sealed class InterruptingInputGuardrail(AgentInterrupt interrupt) : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Interrupt(interrupt));
    }

    private sealed class InterruptingOutputGuardrail(AgentInterrupt interrupt) : IOutputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionResponse response, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Interrupt(interrupt));
    }

    private sealed class BrokenInterruptGuardrail : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new GuardrailOutcome(GuardrailDecision.Interrupt, "no payload"));
    }
}
