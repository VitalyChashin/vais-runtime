// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class TerminationConditionsTests
{
    [Fact]
    public async Task FromPredicate_Wraps_Delegate_As_ITerminationCondition()
    {
        TerminationPredicate predicate = steps => steps.Count >= 3;
        var condition = TerminationConditions.FromPredicate(predicate);

        var twoSteps = new[]
        {
            new OrchestrationStep("a", "x"),
            new OrchestrationStep("b", "y"),
        };
        (await condition.ShouldTerminateAsync(twoSteps)).Should().BeFalse();

        var threeSteps = twoSteps.Append(new OrchestrationStep("c", "z")).ToArray();
        (await condition.ShouldTerminateAsync(threeSteps)).Should().BeTrue();
    }

    [Fact]
    public void FromPredicate_Rejects_Null()
    {
        Action act = () => TerminationConditions.FromPredicate(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

public sealed class RoundRobinOrchestratorITerminationConditionTests
{
    [Fact]
    public async Task ITerminationCondition_Overload_Stops_When_Condition_Returns_True()
    {
        var calls = 0;
        var condition = new AfterNCondition(n: 2, onEval: () => calls++);

        var p1 = new AgentParticipant("a", new FakeProvider("one"));
        var p2 = new AgentParticipant("b", new FakeProvider("two"));
        var p3 = new AgentParticipant("c", new FakeProvider("three"));

        var orchestrator = new RoundRobinOrchestrator(
            new[] { p1, p2, p3 },
            maxRounds: 5,
            terminate: condition);

        var steps = new List<OrchestrationStep>();
        await foreach (var step in orchestrator.RunAsync("go"))
        {
            steps.Add(step);
        }

        steps.Should().HaveCount(2);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task Delegate_Overload_Still_Works_Unchanged()
    {
        TerminationPredicate stopAfterTwo = steps => steps.Count >= 2;
        var p1 = new AgentParticipant("a", new FakeProvider("x"));
        var p2 = new AgentParticipant("b", new FakeProvider("y"));

        var orchestrator = new RoundRobinOrchestrator(
            new[] { p1, p2 },
            maxRounds: 10,
            terminate: stopAfterTwo);

        var steps = new List<OrchestrationStep>();
        await foreach (var step in orchestrator.RunAsync("go"))
        {
            steps.Add(step);
        }
        steps.Should().HaveCount(2);
    }

    [Fact]
    public async Task ITerminationCondition_Sees_Cancellation_Token()
    {
        var p1 = new AgentParticipant("a", new FakeProvider("x"));
        var observed = false;
        var condition = new CapturingCondition(token => observed = token != CancellationToken.None);

        var orchestrator = new RoundRobinOrchestrator(new[] { p1 }, maxRounds: 1, terminate: condition);

        using var cts = new CancellationTokenSource();
        await foreach (var _ in orchestrator.RunAsync("go", cts.Token)) { }

        observed.Should().BeTrue();
    }

    // ---- helpers ----

    private sealed class FakeProvider(string reply) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse(reply));
    }

    private sealed class AfterNCondition(int n, Action? onEval = null) : ITerminationCondition
    {
        public ValueTask<bool> ShouldTerminateAsync(IReadOnlyList<OrchestrationStep> steps, CancellationToken cancellationToken = default)
        {
            onEval?.Invoke();
            return ValueTask.FromResult(steps.Count >= n);
        }
    }

    private sealed class CapturingCondition(Action<CancellationToken> capture) : ITerminationCondition
    {
        public ValueTask<bool> ShouldTerminateAsync(IReadOnlyList<OrchestrationStep> steps, CancellationToken cancellationToken = default)
        {
            capture(cancellationToken);
            return ValueTask.FromResult(true);
        }
    }
}

public sealed class HandoffRecordTests
{
    [Fact]
    public void Record_Equality_Holds_Across_Fields()
    {
        var history = new[] { new ChatTurn(AgentChatRole.User, "hi") };
        var a = new Handoff("src", "dst", "message", history);
        var b = new Handoff("src", "dst", "message", history);

        a.Should().Be(b);
    }

    [Fact]
    public void Record_Equality_Distinguishes_Target()
    {
        var a = new Handoff("src", "dst1");
        var b = new Handoff("src", "dst2");

        a.Should().NotBe(b);
    }

    [Fact]
    public void HandoffRequested_Carries_Handoff_Payload()
    {
        var handoff = new Handoff("src", "dst", "reason", null);
        var @event = new HandoffRequested(DateTimeOffset.UtcNow, AgentContext.Empty, handoff);

        @event.Handoff.Should().BeSameAs(handoff);
        @event.Handoff.FromAgent.Should().Be("src");
    }
}
