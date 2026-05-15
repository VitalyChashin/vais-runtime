// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class ContextContributionTests
{
    [Fact]
    public void Empty_Has_No_Sections_And_Null_Legacy_Views()
    {
        ContextContribution.Empty.Sections.Should().BeEmpty();
        ContextContribution.Empty.SystemPromptAddendum.Should().BeNull();
        ContextContribution.Empty.InjectedHistory.Should().BeNull();
        ContextContribution.Empty.AdditionalTools.Should().BeNull();
    }

    [Fact]
    public void Legacy_Ctor_With_All_Nulls_Yields_Empty_Sections()
    {
        var c = new ContextContribution();

        c.Sections.Should().BeEmpty();
        c.SystemPromptAddendum.Should().BeNull();
        c.InjectedHistory.Should().BeNull();
        c.AdditionalTools.Should().BeNull();
    }

    [Fact]
    public void Legacy_Ctor_With_SystemPromptAddendum_Produces_LegacyAddendum_Section()
    {
        var c = new ContextContribution(SystemPromptAddendum: "extra");

        c.SystemPromptAddendum.Should().Be("extra");
        c.Sections.Should().ContainSingle()
            .Which.Should().Match<Section>(s =>
                s.Id == "system.legacy_addendum"
                && s.Kind == SectionKind.SystemSegment
                && s.ProducerId == "legacy");

        var payload = c.Sections[0].Payload.Should().BeOfType<TextPayload>().Subject;
        payload.Value.Should().Be("extra");
    }

    [Fact]
    public void Legacy_Ctor_With_InjectedHistory_Produces_Indexed_Turn_Sections()
    {
        var turns = new[]
        {
            new ChatTurn(AgentChatRole.User, "first"),
            new ChatTurn(AgentChatRole.Assistant, "second"),
        };

        var c = new ContextContribution(InjectedHistory: turns);

        c.InjectedHistory.Should().BeEquivalentTo(turns);
        c.Sections.Should().HaveCount(2);
        c.Sections[0].Id.Should().Be("history.legacy_injected.0");
        c.Sections[0].Kind.Should().Be(SectionKind.UserMessage);
        c.Sections[0].Order.Should().Be(0);
        c.Sections[0].ProducerId.Should().Be("legacy");
        c.Sections[1].Id.Should().Be("history.legacy_injected.1");
        c.Sections[1].Kind.Should().Be(SectionKind.AssistantMessage);
        c.Sections[1].Order.Should().Be(1);
    }

    [Fact]
    public void Legacy_Ctor_With_AdditionalTools_Produces_LegacyAdditional_Section()
    {
        var tools = new ITool[] { new FakeTool("calc") };

        var c = new ContextContribution(AdditionalTools: tools);

        c.AdditionalTools.Should().BeEquivalentTo(tools);
        c.Sections.Should().ContainSingle()
            .Which.Should().Match<Section>(s =>
                s.Id == "tools.legacy_additional"
                && s.Kind == SectionKind.ToolDeclaration
                && s.ProducerId == "legacy");
    }

    [Fact]
    public void Legacy_Ctor_With_All_Three_Slots_Produces_All_Sections()
    {
        var c = new ContextContribution(
            SystemPromptAddendum: "rules",
            InjectedHistory: new[] { new ChatTurn(AgentChatRole.User, "q") },
            AdditionalTools: new ITool[] { new FakeTool("t") });

        c.Sections.Should().HaveCount(3);
        c.Sections.Select(s => s.Id).Should().Equal(
            "system.legacy_addendum",
            "history.legacy_injected.0",
            "tools.legacy_additional");
        c.Sections.Should().OnlyContain(s => s.ProducerId == "legacy");
    }

    [Fact]
    public void Section_Ctor_Sets_Sections_Verbatim()
    {
        var sections = new[]
        {
            new Section("retrieval.docs", SectionKind.SystemSegment, new TextPayload("hit-1"), ProducerId: "rag"),
        };

        var c = new ContextContribution(sections);

        c.Sections.Should().BeSameAs(sections);
    }

    [Fact]
    public void Section_Ctor_Throws_On_Null()
    {
        Action act = () => new ContextContribution((IReadOnlyList<Section>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SystemPromptAddendum_Aggregates_All_SystemSegment_Sections()
    {
        var sections = new[]
        {
            new Section("system.persona", SectionKind.SystemSegment, new TextPayload("you are helpful"), ProducerId: "persona"),
            new Section("retrieval.docs", SectionKind.SystemSegment, new TextPayload("source 1"), ProducerId: "rag"),
            new Section("system.policy", SectionKind.SystemSegment, new TextPayload(""), ProducerId: "policy"),
        };

        var c = new ContextContribution(sections);

        c.SystemPromptAddendum.Should().Be("you are helpful\n\nsource 1");
    }

    [Fact]
    public void SystemPromptAddendum_Returns_Null_When_No_SystemSegment_Sections()
    {
        var sections = new[]
        {
            new Section("tools.declared", SectionKind.ToolDeclaration, new ToolsPayload(new ITool[] { new FakeTool("t") }), ProducerId: "tools"),
        };

        var c = new ContextContribution(sections);

        c.SystemPromptAddendum.Should().BeNull();
    }

    [Fact]
    public void InjectedHistory_Projects_All_Turn_Sections_In_Order()
    {
        var u = new ChatTurn(AgentChatRole.User, "q1");
        var a = new ChatTurn(AgentChatRole.Assistant, "a1");
        var t = new ChatTurn(AgentChatRole.Tool, "result", ToolCallId: "call-1");

        var sections = new[]
        {
            new Section("history.injected.user", SectionKind.UserMessage, new TurnPayload(u)),
            new Section("history.injected.assistant", SectionKind.AssistantMessage, new TurnPayload(a)),
            new Section("history.injected.tool", SectionKind.ToolMessage, new TurnPayload(t)),
        };

        var c = new ContextContribution(sections);

        c.InjectedHistory.Should().NotBeNull();
        c.InjectedHistory.Should().Equal(u, a, t);
    }

    [Fact]
    public void AdditionalTools_Flattens_All_ToolDeclaration_Sections()
    {
        var t1 = new FakeTool("calc");
        var t2 = new FakeTool("search");

        var sections = new[]
        {
            new Section("tools.calc", SectionKind.ToolDeclaration, new ToolsPayload(new ITool[] { t1 })),
            new Section("tools.search", SectionKind.ToolDeclaration, new ToolsPayload(new ITool[] { t2 })),
        };

        var c = new ContextContribution(sections);

        c.AdditionalTools.Should().NotBeNull();
        c.AdditionalTools.Should().Equal(t1, t2);
    }

    [Fact]
    public void Legacy_Round_Trip_Reads_Back_Identical_Values()
    {
        var turns = new[] { new ChatTurn(AgentChatRole.User, "q") };
        var tools = new ITool[] { new FakeTool("calc") };

        var c = new ContextContribution(
            SystemPromptAddendum: "rules",
            InjectedHistory: turns,
            AdditionalTools: tools);

        c.SystemPromptAddendum.Should().Be("rules");
        c.InjectedHistory.Should().BeEquivalentTo(turns);
        c.AdditionalTools.Should().BeEquivalentTo(tools);
    }

    private sealed class FakeTool(string name) : ITool
    {
        public string Name { get; } = name;
        public string Description => "fake";
        public System.Text.Json.JsonElement ParametersSchema => System.Text.Json.JsonDocument.Parse("{}").RootElement;
        public System.Threading.Tasks.Task<string> InvokeAsync(System.Text.Json.JsonElement arguments, System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult(string.Empty);
    }
}
