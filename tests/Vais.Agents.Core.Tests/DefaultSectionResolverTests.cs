// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class DefaultSectionResolverTests
{
    private readonly DefaultSectionResolver _resolver = new();

    private static Section System(string id, int? order = null, string? producer = null)
        => new(id, SectionKind.SystemSegment, new TextPayload(id), Order: order, ProducerId: producer);

    private static Section User(string id, int? order = null)
        => new(id, SectionKind.UserMessage, new TurnPayload(new ChatTurn(AgentChatRole.User, id)), Order: order);

    private static Section Assistant(string id, int? order = null)
        => new(id, SectionKind.AssistantMessage, new TurnPayload(new ChatTurn(AgentChatRole.Assistant, id)), Order: order);

    private static Section ToolMsg(string id, int? order = null)
        => new(id, SectionKind.ToolMessage, new TurnPayload(new ChatTurn(AgentChatRole.Tool, id, ToolCallId: "call-" + id)), Order: order);

    private static Section ToolDecl(string id, int? order = null, string? producer = null)
        => new(id, SectionKind.ToolDeclaration, new ToolsPayload(Array.Empty<ITool>()), Order: order, ProducerId: producer);

    private static Section Metadata(string id, int? order = null)
        => new(id, SectionKind.Metadata, new MetadataPayload(new Dictionary<string, object?>()), Order: order);

    private static Section ResponseFormat(string id, string? producer = null)
    {
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        return new Section(id, SectionKind.ResponseFormat, new ResponseFormatPayload(new ResponseFormatSpec(schema)), ProducerId: producer);
    }

    [Fact]
    public async Task Empty_Input_Returns_Empty_Output()
    {
        var result = await _resolver.ResolveAsync(Array.Empty<Section>());
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Null_Input_Throws()
    {
        Func<Task> act = async () => await _resolver.ResolveAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Single_Section_Round_Trips()
    {
        var s = System("system.persona");
        var result = await _resolver.ResolveAsync(new[] { s });
        result.Should().Equal(s);
    }

    [Fact]
    public async Task Duplicate_Id_Throws_SectionCollisionException()
    {
        var sections = new[]
        {
            System("retrieval.docs", producer: "rag-a"),
            System("retrieval.docs", producer: "rag-b"),
        };

        Func<Task> act = async () => await _resolver.ResolveAsync(sections);

        var ex = (await act.Should().ThrowAsync<SectionCollisionException>()).Which;
        ex.OffendingKey.Should().Be("retrieval.docs");
        ex.ProducerIds.Should().Equal("rag-a", "rag-b");
        ex.Message.Should().Contain("retrieval.docs").And.Contain("rag-a").And.Contain("rag-b");
    }

    [Fact]
    public async Task Multiple_ResponseFormat_Sections_Throw_Even_With_Different_Ids()
    {
        var sections = new[]
        {
            ResponseFormat("format.response", producer: "first"),
            ResponseFormat("format.alternate", producer: "second"),
        };

        Func<Task> act = async () => await _resolver.ResolveAsync(sections);

        var ex = (await act.Should().ThrowAsync<SectionCollisionException>()).Which;
        ex.OffendingKey.Should().Be("ResponseFormat");
        ex.ProducerIds.Should().Equal("first", "second");
    }

    [Fact]
    public async Task Single_ResponseFormat_Is_Allowed()
    {
        var sections = new[] { ResponseFormat("format.response") };
        var result = await _resolver.ResolveAsync(sections);
        result.Should().ContainSingle().Which.Kind.Should().Be(SectionKind.ResponseFormat);
    }

    [Fact]
    public async Task Cross_Kind_Order_Is_Canonical()
    {
        // Intentionally reversed input order.
        var input = new[]
        {
            Metadata("meta.x"),
            ResponseFormat("format.response"),
            ToolDecl("tools.declared"),
            User("history.q"),
            System("system.persona"),
        };

        var result = await _resolver.ResolveAsync(input);

        result.Select(s => s.Kind).Should().Equal(
            SectionKind.SystemSegment,
            SectionKind.UserMessage,
            SectionKind.ToolDeclaration,
            SectionKind.ResponseFormat,
            SectionKind.Metadata);
    }

    [Fact]
    public async Task Within_SystemSegment_Sorts_By_Order_Ascending()
    {
        var input = new[]
        {
            System("system.late", order: 10),
            System("system.early", order: 0),
            System("system.mid", order: 5),
        };

        var result = await _resolver.ResolveAsync(input);

        result.Select(s => s.Id).Should().Equal("system.early", "system.mid", "system.late");
    }

    [Fact]
    public async Task Null_Order_Falls_Back_To_Registration_Index()
    {
        // null-order sections sit at their registration index. Explicit Order=2 ties with
        // the third section's effective order (its index), and registration order breaks the tie.
        var input = new[]
        {
            System("a"),               // null order, index 0 → effective 0
            System("b", order: 2),     // effective 2
            System("c"),               // null order, index 2 → effective 2 (tie with b; b registered first)
        };

        var result = await _resolver.ResolveAsync(input);

        result.Select(s => s.Id).Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Stable_Sort_Preserves_Registration_Order_On_Equal_Effective_Order()
    {
        var input = new[]
        {
            System("first", order: 5),
            System("second", order: 5),
            System("third", order: 5),
        };

        var result = await _resolver.ResolveAsync(input);

        result.Select(s => s.Id).Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task Turn_Kinds_Interleave_By_Order_Not_By_Kind()
    {
        // The plan: User/Assistant/Tool messages are one interleaved group sorted by Order.
        var input = new[]
        {
            Assistant("a-late", order: 20),
            User("u-early", order: 10),
            ToolMsg("t-mid", order: 15),
            User("u-after-tool", order: 17),
        };

        var result = await _resolver.ResolveAsync(input);

        result.Select(s => s.Id).Should().Equal("u-early", "t-mid", "u-after-tool", "a-late");
    }

    [Fact]
    public async Task Metadata_Always_Last_Even_With_Negative_Order()
    {
        var input = new[]
        {
            Metadata("meta.x", order: -100),
            User("u", order: 100),
            System("sys", order: 100),
        };

        var result = await _resolver.ResolveAsync(input);

        result.Last().Id.Should().Be("meta.x");
    }

    [Fact]
    public async Task Full_Canonical_Example_Returns_Expected_Order()
    {
        // A representative kitchen-sink turn: persona + policy + retrieval + history (turns) + tools + format + metadata.
        var input = new[]
        {
            Metadata("trace.metadata"),
            System("retrieval.docs", order: 20, producer: "rag"),
            Assistant("history.assistant.0", order: 1),
            ToolDecl("tools.declared", producer: "tools"),
            System("system.persona", order: 0, producer: "persona"),
            ResponseFormat("format.response", producer: "manifest"),
            System("system.policy", order: 10, producer: "policy"),
            User("history.user.0", order: 0),
        };

        var result = await _resolver.ResolveAsync(input);

        result.Select(s => s.Id).Should().Equal(
            "system.persona",
            "system.policy",
            "retrieval.docs",
            "history.user.0",
            "history.assistant.0",
            "tools.declared",
            "format.response",
            "trace.metadata");
    }
}
