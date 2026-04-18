// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class SequentialOrchestratorTests
{
    [Fact]
    public async Task Pipelines_Each_Participant_With_Previous_Output()
    {
        // Three participants that each append their name to whatever they receive.
        // Drives the "each stage sees previous stage's text as input" invariant.
        var alice = new RecordingProvider("Alice", userMsg => $"{userMsg}+alice");
        var bob = new RecordingProvider("Bob", userMsg => $"{userMsg}+bob");
        var carol = new RecordingProvider("Carol", userMsg => $"{userMsg}+carol");
        var orch = new SequentialOrchestrator(new[]
        {
            new AgentParticipant("A", alice),
            new AgentParticipant("B", bob),
            new AgentParticipant("C", carol),
        });

        var steps = new List<OrchestrationStep>();
        await foreach (var s in orch.RunAsync("seed"))
        {
            steps.Add(s);
        }

        steps.Select(s => (s.AgentName, s.Text)).Should().Equal(
            ("A", "seed+alice"),
            ("B", "seed+alice+bob"),
            ("C", "seed+alice+bob+carol"));

        // Verify each participant's received user message lines up.
        alice.ReceivedUserMessages.Should().Equal("seed");
        bob.ReceivedUserMessages.Should().Equal("seed+alice");
        carol.ReceivedUserMessages.Should().Equal("seed+alice+bob");
    }

    [Fact]
    public async Task Forwards_SystemPrompt_Per_Participant()
    {
        var alice = new RecordingProvider("Alice", _ => "ok-alice");
        var bob = new RecordingProvider("Bob", _ => "ok-bob");
        var orch = new SequentialOrchestrator(new[]
        {
            new AgentParticipant("A", alice, SystemPrompt: "be-alice"),
            new AgentParticipant("B", bob, SystemPrompt: "be-bob"),
        });

        await foreach (var _ in orch.RunAsync("x")) { }

        alice.ReceivedSystemPrompts.Should().Equal("be-alice");
        bob.ReceivedSystemPrompts.Should().Equal("be-bob");
    }

    [Fact]
    public async Task Rejects_Empty_Participant_List_And_Empty_Task()
    {
        Action ctorAct = () => _ = new SequentialOrchestrator(Array.Empty<AgentParticipant>());
        ctorAct.Should().Throw<ArgumentException>().WithParameterName("participants");

        var orch = new SequentialOrchestrator(new[] { new AgentParticipant("A", new RecordingProvider("A", _ => "ok")) });
        Func<Task> runAct = async () => { await foreach (var _ in orch.RunAsync("   ")) { } };
        (await runAct.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("task");
    }

    [Fact]
    public async Task Propagates_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        var first = new RecordingProvider("A", _ => "a");
        var second = new RecordingProvider("B", _ => { cts.Cancel(); return "b"; });
        var third = new RecordingProvider("C", _ => "c");
        var orch = new SequentialOrchestrator(new[]
        {
            new AgentParticipant("A", first),
            new AgentParticipant("B", second),
            new AgentParticipant("C", third),
        });

        Func<Task> act = async () =>
        {
            await foreach (var _ in orch.RunAsync("x", cts.Token)) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        // C must never have been invoked — cancellation fired during B's run.
        third.ReceivedUserMessages.Should().BeEmpty();
    }
}

public sealed class RoundRobinOrchestratorTests
{
    [Fact]
    public async Task Rotates_Through_Participants_For_Each_Round()
    {
        var a = new RecordingProvider("A", _ => "a-reply");
        var b = new RecordingProvider("B", _ => "b-reply");
        var orch = new RoundRobinOrchestrator(
            new[] { new AgentParticipant("A", a), new AgentParticipant("B", b) },
            maxRounds: 2);

        var steps = new List<OrchestrationStep>();
        await foreach (var s in orch.RunAsync("task"))
        {
            steps.Add(s);
        }

        steps.Select(s => s.AgentName).Should().Equal("A", "B", "A", "B");
    }

    [Fact]
    public async Task Each_Participant_Sees_Shared_History_With_Labeled_Prior_Steps()
    {
        var a = new RecordingProvider("A", _ => "hello");
        var b = new RecordingProvider("B", _ => "hi");
        var orch = new RoundRobinOrchestrator(
            new[] { new AgentParticipant("A", a), new AgentParticipant("B", b) },
            maxRounds: 1);

        await foreach (var _ in orch.RunAsync("discuss")) { }

        // A was the first participant — sees only the user task.
        a.ReceivedHistories.Should().ContainSingle();
        a.ReceivedHistories[0].Select(t => (t.Role, t.Text)).Should().Equal(
            (AgentChatRole.User, "discuss"));

        // B saw the same user task + A's labeled prior reply.
        b.ReceivedHistories.Should().ContainSingle();
        b.ReceivedHistories[0].Select(t => (t.Role, t.Text)).Should().Equal(
            (AgentChatRole.User, "discuss"),
            (AgentChatRole.Assistant, "[A] hello"));
    }

    [Fact]
    public async Task TerminationPredicate_Short_Circuits_Mid_Round()
    {
        var a = new RecordingProvider("A", _ => "finish");
        var b = new RecordingProvider("B", _ => "shouldn't-run");
        // Terminate as soon as any step's text is "finish".
        var orch = new RoundRobinOrchestrator(
            new[] { new AgentParticipant("A", a), new AgentParticipant("B", b) },
            maxRounds: 5,
            terminate: steps => steps[^1].Text == "finish");

        var steps = new List<OrchestrationStep>();
        await foreach (var s in orch.RunAsync("x"))
        {
            steps.Add(s);
        }

        steps.Should().ContainSingle();
        steps[0].AgentName.Should().Be("A");
        b.ReceivedUserMessages.Should().BeEmpty();
    }

    [Fact]
    public void Rejects_Invalid_Construction()
    {
        var provider = new RecordingProvider("x", _ => "ok");
        Action empty = () => _ = new RoundRobinOrchestrator(Array.Empty<AgentParticipant>(), 1);
        empty.Should().Throw<ArgumentException>().WithParameterName("participants");

        Action zeroRounds = () => _ = new RoundRobinOrchestrator(new[] { new AgentParticipant("X", provider) }, 0);
        zeroRounds.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxRounds");
    }
}

/// <summary>
/// Test double: records every request and returns a caller-supplied reply for each.
/// Tracks the per-call user message, system prompt, and history for assertion.
/// </summary>
internal sealed class RecordingProvider : ICompletionProvider
{
    private readonly string _name;
    private readonly Func<string, string> _reply;

    public RecordingProvider(string name, Func<string, string> reply)
    {
        _name = name;
        _reply = reply;
    }

    public List<string> ReceivedUserMessages { get; } = new();
    public List<string?> ReceivedSystemPrompts { get; } = new();
    public List<IReadOnlyList<ChatTurn>> ReceivedHistories { get; } = new();

    public string ProviderName => _name;

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        // The most recent user turn is what this provider treats as its "input".
        var userMsg = request.History.LastOrDefault(t => t.Role == AgentChatRole.User)?.Text ?? string.Empty;
        ReceivedUserMessages.Add(userMsg);
        ReceivedSystemPrompts.Add(request.SystemPrompt);
        ReceivedHistories.Add(request.History.ToArray());
        return Task.FromResult(new CompletionResponse(_reply(userMsg)));
    }
}
