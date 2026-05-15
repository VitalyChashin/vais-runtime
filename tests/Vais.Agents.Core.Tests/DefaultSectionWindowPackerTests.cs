// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class DefaultSectionWindowPackerTests
{
    private readonly DefaultSectionWindowPacker _packer = new();

    private static Section Sys(string id, string text, int? priority = null, int? order = null)
        => new(
            id,
            SectionKind.SystemSegment,
            new TextPayload(text),
            Order: order,
            Budget: priority is null ? null : new SectionBudget(priority.Value));

    private static Section Meta(string id, string filler)
        => new(id, SectionKind.Metadata, new MetadataPayload(new Dictionary<string, object?> { ["filler"] = filler }));

    [Fact]
    public async Task Null_Sections_Throws()
    {
        Func<Task> act = async () => await _packer.PackAsync(null!, SectionBudgetContext.Unlimited);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Null_Budget_Throws()
    {
        Func<Task> act = async () => await _packer.PackAsync(Array.Empty<Section>(), null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Empty_Sections_Returns_Empty_Result()
    {
        var result = await _packer.PackAsync(Array.Empty<Section>(), SectionBudgetContext.Unlimited);

        result.Sections.Should().BeEmpty();
        result.Outcomes.Should().BeEmpty();
    }

    [Fact]
    public async Task Unlimited_Budget_Returns_Identity_All_Included()
    {
        var input = new[] { Sys("a", "hello"), Sys("b", "world") };

        var result = await _packer.PackAsync(input, SectionBudgetContext.Unlimited);

        result.Sections.Should().BeSameAs(input);
        result.Outcomes.Should().HaveCount(2);
        result.Outcomes.Should().OnlyContain(o => o.Outcome == PackerOutcomes.Included);
    }

    [Fact]
    public async Task Under_Budget_Returns_Identity()
    {
        var input = new[] { Sys("a", "12345"), Sys("b", "67890") };  // 10 chars total

        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 100));

        result.Sections.Should().BeSameAs(input);
        result.Outcomes.Should().HaveCount(2);
        result.Outcomes.Should().OnlyContain(o => o.Outcome == PackerOutcomes.Included);
    }

    [Fact]
    public async Task Over_Budget_Drops_Highest_Priority_Number_First()
    {
        // Sizes: critical=8, important=8, optional=8 (24 total). Budget = 16. Should drop "optional" (pri=8).
        var input = new[]
        {
            Sys("critical", "12345678", priority: 0),
            Sys("important", "abcdefgh", priority: 3),
            Sys("optional", "ZZZZZZZZ", priority: 8),
        };

        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 16));

        result.Sections.Select(s => s.Id).Should().Equal("critical", "important");
        result.Outcomes.Should().HaveCount(3);
        result.Outcomes.Single(o => o.SectionId == "optional").Outcome.Should().Be(PackerOutcomes.Dropped);
        result.Outcomes.Single(o => o.SectionId == "optional").DroppedChars.Should().Be(8);
        result.Outcomes.Where(o => o.SectionId != "optional").Should().OnlyContain(o => o.Outcome == PackerOutcomes.Included);
    }

    [Fact]
    public async Task Equal_Priority_Drops_Largest_Section_First()
    {
        // All priority 5; budget 10. Sizes: tiny=4, big=20, mid=10 → total 34, must shed ≥ 24.
        // Drop biggest first: big (20). Remaining: tiny+mid=14 still > 10. Drop next biggest: mid (10).
        var input = new[]
        {
            Sys("tiny", "abcd", priority: 5),
            Sys("big", new string('X', 20), priority: 5),
            Sys("mid", "0123456789", priority: 5),
        };

        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 10));

        result.Sections.Select(s => s.Id).Should().Equal("tiny");
        result.Outcomes.Single(o => o.SectionId == "big").Outcome.Should().Be(PackerOutcomes.Dropped);
        result.Outcomes.Single(o => o.SectionId == "mid").Outcome.Should().Be(PackerOutcomes.Dropped);
    }

    [Fact]
    public async Task Equal_Priority_And_Size_Stable_Drops_By_Registration_Order()
    {
        // Three sections, all priority 5, all size 10. Budget 25. Must drop 1. Stable: drop first.
        var input = new[]
        {
            Sys("first", "0123456789", priority: 5),
            Sys("second", "0123456789", priority: 5),
            Sys("third", "0123456789", priority: 5),
        };

        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 25));

        result.Sections.Select(s => s.Id).Should().Equal("second", "third");
        result.Outcomes.Single(o => o.SectionId == "first").Outcome.Should().Be(PackerOutcomes.Dropped);
    }

    [Fact]
    public async Task Priority_Zero_Is_Never_Dropped_Even_Over_Budget()
    {
        var input = new[]
        {
            Sys("critical-a", new string('A', 100), priority: 0),
            Sys("critical-b", new string('B', 100), priority: 0),
        };

        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 50));

        result.Sections.Select(s => s.Id).Should().Equal("critical-a", "critical-b");
        result.Outcomes.Should().OnlyContain(o => o.Outcome == PackerOutcomes.Included);
    }

    [Fact]
    public async Task No_Budget_Set_Returns_Identity()
    {
        var input = new[] { Sys("a", new string('X', 1000)) };

        // MaxChars null, MaxTokens null → no budget → identity.
        var result = await _packer.PackAsync(input, new SectionBudgetContext());

        result.Sections.Should().BeSameAs(input);
    }

    [Fact]
    public async Task MaxTokens_Without_Counter_Falls_Through_To_MaxChars()
    {
        var input = new[]
        {
            Sys("a", "12345", priority: 5),
            Sys("b", "67890", priority: 8),
        };

        // MaxTokens set but no counter → packer treats MaxTokens as unset; falls back to MaxChars.
        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 5, MaxTokens: 1));

        result.Sections.Select(s => s.Id).Should().Equal("a");
    }

    [Fact]
    public async Task Token_Budget_Uses_Counter_For_Sizing()
    {
        // Counter reports half the char count → packer thinks sections are smaller.
        var counter = new FakeTokenCounter(charsPerToken: 2);
        var input = new[]
        {
            Sys("a", "12345678", priority: 5),     // 8 chars → 4 tokens
            Sys("b", "abcdefgh", priority: 8),     // 8 chars → 4 tokens
        };

        // Budget 6 tokens. Total 8 tokens. Must drop one. Higher priority number wins (b).
        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxTokens: 6, TokenCounter: counter));

        result.Sections.Select(s => s.Id).Should().Equal("a");
        result.Outcomes.Single(o => o.SectionId == "b").Outcome.Should().Be(PackerOutcomes.Dropped);
        // DroppedChars reports char length, not token count.
        result.Outcomes.Single(o => o.SectionId == "b").DroppedChars.Should().Be(8);
    }

    [Fact]
    public async Task Metadata_Exempt_From_Budget()
    {
        var input = new[]
        {
            Sys("a", "12345", priority: 5),
            Meta("trace.metadata", new string('M', 10_000)),
        };

        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 10));

        result.Sections.Should().HaveCount(2);
        result.Outcomes.Should().OnlyContain(o => o.Outcome == PackerOutcomes.Included);
    }

    [Fact]
    public async Task Outcome_List_Matches_Input_Order_And_Count()
    {
        var input = new[]
        {
            Sys("a", "x", priority: 0),
            Sys("b", new string('B', 50), priority: 7),
            Sys("c", "y", priority: 0),
            Sys("d", new string('D', 50), priority: 7),
        };

        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 30));

        result.Outcomes.Should().HaveCount(4);
        result.Outcomes.Select(o => o.SectionId).Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public async Task Default_Priority_Five_Is_Eligible_For_Drop()
    {
        // No explicit Budget → DefaultPriority (5) → eligible. Critical priority-0 stays.
        var input = new[]
        {
            Sys("critical", "1234567890", priority: 0),
            Sys("default-priority", "abcdefghij"),  // no budget → priority 5
        };

        var result = await _packer.PackAsync(input, new SectionBudgetContext(MaxChars: 12));

        result.Sections.Select(s => s.Id).Should().Equal("critical");
        result.Outcomes.Single(o => o.SectionId == "default-priority").Outcome.Should().Be(PackerOutcomes.Dropped);
    }

    private sealed class FakeTokenCounter(int charsPerToken) : ITokenCounter
    {
        public int Count(string text) => text.Length / charsPerToken;
    }
}
