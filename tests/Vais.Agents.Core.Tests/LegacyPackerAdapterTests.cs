// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class LegacyPackerAdapterTests
{
    private static Section Sys(string id, string text)
        => new(id, SectionKind.SystemSegment, new TextPayload(text));

    private static Section User(string id, string text)
        => new(id, SectionKind.UserMessage, new TurnPayload(new ChatTurn(AgentChatRole.User, text)));

    private static Section Assistant(string id, string text)
        => new(id, SectionKind.AssistantMessage, new TurnPayload(new ChatTurn(AgentChatRole.Assistant, text)));

    private static Section Meta(string id)
        => new(id, SectionKind.Metadata, new MetadataPayload(new Dictionary<string, object?>()));

    [Fact]
    public void Null_Legacy_Packer_Throws()
    {
        Action act = () => new LegacyPackerAdapter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Empty_Sections_Returns_Empty_Result()
    {
        var adapter = new LegacyPackerAdapter(NoopContextWindowPacker.Instance);

        var result = await adapter.PackAsync(Array.Empty<Section>(), SectionBudgetContext.Unlimited);

        result.Sections.Should().BeEmpty();
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task Noop_Packer_Round_Trips_Sections_With_Legacy_Outcomes()
    {
        var adapter = new LegacyPackerAdapter(NoopContextWindowPacker.Instance);
        var input = new[]
        {
            Sys("system.persona", "persona"),
            User("history.user.0", "q"),
            Assistant("history.assistant.0", "a"),
        };

        var result = await adapter.PackAsync(input, SectionBudgetContext.Unlimited);

        result.Sections.Should().BeSameAs(input);
        result.Outcomes.Should().HaveCount(3);
        result.Outcomes.Should().OnlyContain(o => o.Outcome == PackerOutcomes.Legacy);
        result.Outcomes.Select(o => o.SectionId).Should().Equal("system.persona", "history.user.0", "history.assistant.0");
    }

    [Fact]
    public async Task Legacy_Packer_Dropping_Oldest_Turn_Drops_The_Matching_Section()
    {
        // Legacy packer that drops the first history turn (typical "trim oldest" strategy).
        var adapter = new LegacyPackerAdapter(new DropFirstTurnPacker());
        var input = new[]
        {
            Sys("system.persona", "p"),
            User("history.user.0", "old"),
            Assistant("history.assistant.0", "kept"),
            User("history.user.1", "kept-too"),
        };

        var result = await adapter.PackAsync(input, SectionBudgetContext.Unlimited);

        result.Sections.Select(s => s.Id).Should().Equal(
            "system.persona", "history.assistant.0", "history.user.1");
        result.Outcomes.Single(o => o.SectionId == "history.user.0").Outcome.Should().Be(PackerOutcomes.Dropped);
        result.Outcomes.Where(o => o.SectionId != "history.user.0")
            .Should().OnlyContain(o => o.Outcome == PackerOutcomes.Legacy);
    }

    [Fact]
    public async Task Legacy_Packer_Clearing_SystemPrompt_Drops_All_SystemSegment_Sections()
    {
        var adapter = new LegacyPackerAdapter(new ClearSystemPromptPacker());
        var input = new[]
        {
            Sys("system.persona", "persona text"),
            Sys("retrieval.docs", "rag text"),
            User("history.user.0", "q"),
        };

        var result = await adapter.PackAsync(input, SectionBudgetContext.Unlimited);

        result.Sections.Select(s => s.Id).Should().Equal("history.user.0");
        result.Outcomes.Single(o => o.SectionId == "system.persona").Outcome.Should().Be(PackerOutcomes.Dropped);
        result.Outcomes.Single(o => o.SectionId == "retrieval.docs").Outcome.Should().Be(PackerOutcomes.Dropped);
        result.Outcomes.Single(o => o.SectionId == "history.user.0").Outcome.Should().Be(PackerOutcomes.Legacy);
    }

    [Fact]
    public async Task Metadata_Sections_Always_Survive_Even_When_Legacy_Drops_Everything()
    {
        var adapter = new LegacyPackerAdapter(new DropEverythingPacker());
        var input = new[]
        {
            Sys("system.persona", "p"),
            User("history.u", "q"),
            Meta("trace.metadata"),
        };

        var result = await adapter.PackAsync(input, SectionBudgetContext.Unlimited);

        result.Sections.Select(s => s.Id).Should().Equal("trace.metadata");
        result.Outcomes.Single(o => o.SectionId == "trace.metadata").Outcome.Should().Be(PackerOutcomes.Legacy);
    }

    // ─────────────────── Fake legacy packers ───────────────────

    private sealed class DropFirstTurnPacker : IContextWindowPacker
    {
        public ValueTask<CompletionRequest> PackAsync(CompletionRequest candidate, CancellationToken cancellationToken = default)
        {
            if (candidate.History.Count == 0)
            {
                return ValueTask.FromResult(candidate);
            }

            var trimmed = candidate.History.Skip(1).ToList();
            return ValueTask.FromResult(candidate with { History = trimmed });
        }
    }

    private sealed class ClearSystemPromptPacker : IContextWindowPacker
    {
        public ValueTask<CompletionRequest> PackAsync(CompletionRequest candidate, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(candidate with { SystemPrompt = null });
    }

    private sealed class DropEverythingPacker : IContextWindowPacker
    {
        public ValueTask<CompletionRequest> PackAsync(CompletionRequest candidate, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(candidate with
            {
                History = Array.Empty<ChatTurn>(),
                SystemPrompt = null,
                Tools = null,
                ResponseFormat = null,
            });
    }
}
