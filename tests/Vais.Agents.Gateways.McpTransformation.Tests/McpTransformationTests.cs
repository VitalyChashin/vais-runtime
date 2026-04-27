// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Gateways.McpTransformation;
using Xunit;

namespace Vais.Agents.Gateways.McpTransformation.Tests;

public sealed class McpTransformationTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static ToolGatewayContext MakeContext(string toolName = "tool", string callId = "c1")
        => new(toolName, callId, EmptyArgs, AgentContext.Empty);

    // ── ToolJsonRepairMiddleware ─────────────────────────────────────────────

    [Fact]
    public async Task JsonRepair_Valid_Json_Returns_Unchanged()
    {
        var mw = new ToolJsonRepairMiddleware();
        var ctx = MakeContext();
        var outcome = new ToolCallOutcome("c1", "{\"key\":\"value\"}");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(outcome);

        var result = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        result.Should().Be(outcome);
    }

    [Fact]
    public async Task JsonRepair_Invalid_Json_Returns_Outcome_Unchanged_When_Repair_Fails()
    {
        var mw = new ToolJsonRepairMiddleware();
        var ctx = MakeContext();
        var outcome = new ToolCallOutcome("c1", "not json at all {{{{");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(outcome);

        var result = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        // AttemptRepair returns null (stub), so outcome is returned unchanged
        result.Should().Be(outcome);
    }

    [Fact]
    public async Task JsonRepair_Error_Outcome_Passes_Through_Without_Parsing()
    {
        var mw = new ToolJsonRepairMiddleware();
        var ctx = MakeContext();
        var errorOutcome = new ToolCallOutcome("c1", "some error message", "SomeError");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(errorOutcome);

        var result = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        result.Should().Be(errorOutcome);
    }

    // ── ToolHtmlToMarkdownMiddleware ─────────────────────────────────────────

    [Fact]
    public async Task HtmlToMarkdown_Html_Content_Is_Converted()
    {
        var mw = new ToolHtmlToMarkdownMiddleware();
        var ctx = MakeContext();
        Func<Task<ToolCallOutcome>> next = () =>
            Task.FromResult(new ToolCallOutcome("c1", "<p>Hello <b>world</b></p>"));

        var result = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        result.Error.Should().BeNull();
        // Markdig ToPlainText strips tags; result should not contain raw HTML tags
        result.Result.Should().NotContain("<p>").And.NotContain("<b>");
        result.Result.Should().Contain("Hello");
    }

    [Fact]
    public async Task HtmlToMarkdown_NonHtml_Content_Is_Unchanged()
    {
        var mw = new ToolHtmlToMarkdownMiddleware();
        var ctx = MakeContext();
        const string plain = "This is plain text without any HTML tags.";
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", plain));

        var result = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        result.Result.Should().Be(plain);
    }

    [Fact]
    public async Task HtmlToMarkdown_Error_Outcome_Passes_Through()
    {
        var mw = new ToolHtmlToMarkdownMiddleware();
        var ctx = MakeContext();
        var errorOutcome = new ToolCallOutcome("c1", "<html><body>error</body></html>", "SomeError");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(errorOutcome);

        var result = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        result.Should().Be(errorOutcome);
    }
}
