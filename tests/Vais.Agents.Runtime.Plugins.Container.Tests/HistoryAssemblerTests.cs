// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Runtime.Plugins.Container.Preprocessing;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

public sealed class HistoryAssemblerTests
{
    private static readonly AgentManifest s_manifest =
        new("test-agent", "1.0", new AgentHandlerRef("Test"), [], []);

    private static AgentPreprocessorContext MakeContext(IAgentGrainStateView grainState) =>
        new("test-agent", "session-1", s_manifest, grainState, AgentContext.Empty);

    [Fact]
    public void Order_Is_Zero()
    {
        new HistoryAssembler().Order.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_EmptyHistory_ReturnsSameInstance()
    {
        var assembler = new HistoryAssembler();
        IReadOnlyList<ChatTurn> seed = [new ChatTurn(AgentChatRole.User, "hello")];
        var ctx = MakeContext(new FakeGrainState([]));

        var result = await assembler.ProcessAsync(ctx, seed);

        result.Should().BeSameAs(seed);
    }

    [Fact]
    public async Task ProcessAsync_TwoTurnHistory_PrependsBothBeforeUserTurn()
    {
        var assembler = new HistoryAssembler();
        var history = new[]
        {
            new ChatTurn(AgentChatRole.User, "user-1"),
            new ChatTurn(AgentChatRole.Assistant, "asst-1"),
        };
        var seed = new[] { new ChatTurn(AgentChatRole.User, "user-2") };
        var ctx = MakeContext(new FakeGrainState(history));

        var result = await assembler.ProcessAsync(ctx, seed);

        result.Should().HaveCount(3);
        result[0].Text.Should().Be("user-1");
        result[1].Text.Should().Be("asst-1");
        result[2].Text.Should().Be("user-2");
    }

    [Fact]
    public async Task ProcessAsync_LongHistory_OrderPreserved()
    {
        var assembler = new HistoryAssembler();
        var history = Enumerable.Range(0, 10)
            .Select(i => new ChatTurn(AgentChatRole.User, $"turn-{i}"))
            .ToArray();
        var seed = new[] { new ChatTurn(AgentChatRole.User, "current") };
        var ctx = MakeContext(new FakeGrainState(history));

        var result = await assembler.ProcessAsync(ctx, seed);

        result.Should().HaveCount(11);
        for (var i = 0; i < 10; i++)
            result[i].Text.Should().Be($"turn-{i}");
        result[10].Text.Should().Be("current");
    }

    [Fact]
    public async Task ProcessAsync_DoesNotMutateInputList()
    {
        var assembler = new HistoryAssembler();
        var history = new[] { new ChatTurn(AgentChatRole.User, "h1") };
        IReadOnlyList<ChatTurn> seed = [new ChatTurn(AgentChatRole.User, "current")];
        var ctx = MakeContext(new FakeGrainState(history));

        await assembler.ProcessAsync(ctx, seed);

        seed.Should().HaveCount(1);
    }

    private sealed class FakeGrainState : IAgentGrainStateView
    {
        private readonly IReadOnlyList<ChatTurn> _history;

        public FakeGrainState(IReadOnlyList<ChatTurn> history) => _history = history;

        public string? SystemPrompt => null;
        public IReadOnlyList<ChatTurn> History => _history;
        public string? OpaqueState => null;
    }
}
