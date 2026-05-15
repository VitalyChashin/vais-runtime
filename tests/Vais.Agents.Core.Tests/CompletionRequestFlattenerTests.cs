// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class CompletionRequestFlattenerTests
{
    // ─────────────────── Section factories ───────────────────

    private static Section SystemSeg(string id, string text, int? order = null, string? producer = null)
        => new(id, SectionKind.SystemSegment, new TextPayload(text), Order: order, ProducerId: producer);

    private static Section UserTurn(string id, string text, int? order = null)
        => new(id, SectionKind.UserMessage, new TurnPayload(new ChatTurn(AgentChatRole.User, text)), Order: order);

    private static Section AssistantTurn(string id, string text, int? order = null)
        => new(id, SectionKind.AssistantMessage, new TurnPayload(new ChatTurn(AgentChatRole.Assistant, text)), Order: order);

    private static Section ToolTurn(string id, string text, string toolCallId)
        => new(id, SectionKind.ToolMessage, new TurnPayload(new ChatTurn(AgentChatRole.Tool, text, ToolCallId: toolCallId)));

    private static Section ToolDecl(string id, params ITool[] tools)
        => new(id, SectionKind.ToolDeclaration, new ToolsPayload(tools));

    private static Section ResponseFormatSection(string id, JsonElement schema)
        => new(id, SectionKind.ResponseFormat, new ResponseFormatPayload(new ResponseFormatSpec(schema)));

    private static Section MetadataSection(string id, IReadOnlyDictionary<string, object?>? values = null)
        => new(id, SectionKind.Metadata, new MetadataPayload(values ?? new Dictionary<string, object?>()));

    private static JsonElement SchemaObject()
        => JsonDocument.Parse("""{"type":"object"}""").RootElement;

    // ─────────────────── Five canonical fixtures ───────────────────

    [Fact]
    public void Persona_Only()
    {
        var sections = new[] { SystemSeg("system.persona", "You are a helpful assistant.") };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.History.Should().BeEmpty();
        result.SystemPrompt.Should().Be("You are a helpful assistant.");
        result.Tools.Should().BeNull();
        result.ResponseFormat.Should().BeNull();
        result.Temperature.Should().BeNull();
        result.MaxTokens.Should().BeNull();
    }

    [Fact]
    public void Persona_Plus_Rag()
    {
        var sections = new[]
        {
            SystemSeg("system.persona", "You are a research assistant."),
            SystemSeg("retrieval.docs", "Source 1: Climate change is accelerating."),
        };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.SystemPrompt.Should().Be("You are a research assistant.\n\nSource 1: Climate change is accelerating.");
        result.History.Should().BeEmpty();
        result.Tools.Should().BeNull();
        result.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public void History_Plus_Tools()
    {
        var calc = new FakeTool("calc", "Math helper.");
        var search = new FakeTool("search", "Web search.");
        var sections = new[]
        {
            UserTurn("history.user.0", "What's 2+2?"),
            AssistantTurn("history.assistant.0", "Using calc."),
            ToolDecl("tools.declared", calc, search),
        };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.SystemPrompt.Should().BeNull();
        result.History.Should().HaveCount(2);
        result.History[0].Role.Should().Be(AgentChatRole.User);
        result.History[0].Text.Should().Be("What's 2+2?");
        result.History[1].Role.Should().Be(AgentChatRole.Assistant);
        result.Tools.Should().NotBeNull().And.HaveCount(2);
        result.Tools!.Select(t => t.Name).Should().Equal("calc", "search");
        result.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public void Response_Format()
    {
        var schema = SchemaObject();
        var sections = new[] { ResponseFormatSection("format.response", schema) };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.ResponseFormat.Should().NotBeNull();
        result.ResponseFormat!.Schema.GetRawText().Should().Be(schema.GetRawText());
        result.SystemPrompt.Should().BeNull();
        result.History.Should().BeEmpty();
        result.Tools.Should().BeNull();
    }

    [Fact]
    public void Full_Kitchen_Sink()
    {
        var schema = SchemaObject();
        var calc = new FakeTool("calc", "Math helper.");
        var sections = new[]
        {
            SystemSeg("system.persona", "Persona."),
            SystemSeg("retrieval.docs", "Retrieval hit."),
            UserTurn("history.user.0", "Q"),
            AssistantTurn("history.assistant.0", "A"),
            ToolTurn("history.tool.0", "tool-result", "call-1"),
            ToolDecl("tools.declared", calc),
            ResponseFormatSection("format.response", schema),
            MetadataSection("trace.metadata", new Dictionary<string, object?> { ["x"] = 1 }),
        };
        var template = new CompletionRequest(History: Array.Empty<ChatTurn>(), Temperature: 0.4f, MaxTokens: 1024);

        var result = CompletionRequestFlattener.Flatten(sections, template);

        result.SystemPrompt.Should().Be("Persona.\n\nRetrieval hit.");
        result.History.Should().HaveCount(3);
        result.History.Select(t => t.Role).Should().Equal(AgentChatRole.User, AgentChatRole.Assistant, AgentChatRole.Tool);
        result.History[2].ToolCallId.Should().Be("call-1");
        result.Tools.Should().NotBeNull().And.ContainSingle(t => t.Name == "calc");
        result.ResponseFormat.Should().NotBeNull();
        result.Temperature.Should().Be(0.4f);
        result.MaxTokens.Should().Be(1024);
    }

    // ─────────────────── Edge cases ───────────────────

    [Fact]
    public void Null_Sections_Throws()
    {
        Action act = () => CompletionRequestFlattener.Flatten(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Empty_Sections_Returns_Empty_Request()
    {
        var result = CompletionRequestFlattener.Flatten(Array.Empty<Section>());

        result.History.Should().BeEmpty();
        result.SystemPrompt.Should().BeNull();
        result.Tools.Should().BeNull();
        result.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public void Empty_SystemSegment_Text_Is_Skipped()
    {
        var sections = new[]
        {
            SystemSeg("system.empty", ""),
            SystemSeg("system.real", "real text"),
        };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.SystemPrompt.Should().Be("real text");
    }

    [Fact]
    public void All_Empty_SystemSegments_Return_Null_SystemPrompt()
    {
        var sections = new[] { SystemSeg("system.empty.1", ""), SystemSeg("system.empty.2", "") };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.SystemPrompt.Should().BeNull();
    }

    [Fact]
    public void Metadata_Sections_Are_Ignored()
    {
        var sections = new[]
        {
            SystemSeg("system.persona", "persona"),
            MetadataSection("trace.metadata", new Dictionary<string, object?> { ["should_not_appear"] = "x" }),
        };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.SystemPrompt.Should().Be("persona");
        result.History.Should().BeEmpty();
        result.Tools.Should().BeNull();
        result.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public void Tool_Name_Dedup_Keeps_Last_Occurrence()
    {
        var first = new FakeTool("calc", "first version");
        var second = new FakeTool("calc", "second version");
        var sections = new[]
        {
            ToolDecl("tools.first", first),
            ToolDecl("tools.second", second),
        };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.Tools.Should().ContainSingle();
        result.Tools![0].Description.Should().Be("second version");
    }

    [Fact]
    public void Template_Carries_Only_Temperature_And_MaxTokens()
    {
        // Template's History / SystemPrompt / Tools / ResponseFormat are deliberately ignored —
        // sections are authoritative for shape. Only sampling hints flow through.
        var unusedHistory = new[] { new ChatTurn(AgentChatRole.User, "ignored") };
        var template = new CompletionRequest(
            History: unusedHistory,
            SystemPrompt: "ignored",
            Temperature: 0.7f,
            MaxTokens: 256,
            Tools: new ITool[] { new FakeTool("ignored", "x") });

        var sections = new[] { SystemSeg("system.persona", "real") };

        var result = CompletionRequestFlattener.Flatten(sections, template);

        result.SystemPrompt.Should().Be("real");
        result.History.Should().BeEmpty();
        result.Tools.Should().BeNull();
        result.Temperature.Should().Be(0.7f);
        result.MaxTokens.Should().Be(256);
    }

    [Fact]
    public void History_Preserves_Resolved_Order_Including_Tool_Turns()
    {
        // The flattener does not re-sort; it walks the input in order. The resolver is responsible
        // for placing turns in the right interleaving — this test asserts the flattener trusts that.
        var sections = new[]
        {
            UserTurn("history.u.0", "q1"),
            AssistantTurn("history.a.0", "a1"),
            ToolTurn("history.t.0", "result", "call-1"),
            AssistantTurn("history.a.1", "a2"),
        };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.History.Select(t => (t.Role, t.Text)).Should().Equal(
            (AgentChatRole.User, "q1"),
            (AgentChatRole.Assistant, "a1"),
            (AgentChatRole.Tool, "result"),
            (AgentChatRole.Assistant, "a2"));
    }

    [Fact]
    public void Two_ResponseFormat_Sections_Keep_First()
    {
        // Defence-in-depth — the resolver normally rejects this, but if the flattener is called
        // directly (e.g. by a custom pipeline) it falls back to keeping the first.
        var sections = new[]
        {
            ResponseFormatSection("format.first", SchemaObject()),
            ResponseFormatSection("format.second", JsonDocument.Parse("""{"type":"string"}""").RootElement),
        };

        var result = CompletionRequestFlattener.Flatten(sections);

        result.ResponseFormat.Should().NotBeNull();
        result.ResponseFormat!.Schema.GetRawText().Should().Be(SchemaObject().GetRawText());
    }

    // ─────────────────── Fake tool ───────────────────

    private sealed class FakeTool(string name, string description) : ITool
    {
        public string Name { get; } = name;
        public string Description { get; } = description;
        public JsonElement ParametersSchema => JsonDocument.Parse("{}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }
}
