// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Gateways.McpSecurity;
using Xunit;

namespace Vais.Agents.Gateways.McpSecurity.Tests;

public sealed class McpSecurityTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static ToolGatewayContext MakeContext(string toolName = "tool", string callId = "c1",
        JsonElement args = default)
        => new(toolName, callId, args.ValueKind == JsonValueKind.Undefined ? EmptyArgs : args, AgentContext.Empty);

    // ── ToolArgumentValidationMiddleware ────────────────────────────────────

    [Fact]
    public async Task ArgValidation_Missing_Required_Arg_Returns_Denial()
    {
        var required = new Dictionary<string, IReadOnlyList<string>>
        {
            ["search"] = ["query"],
        };
        var mw = new ToolArgumentValidationMiddleware(required);
        var ctx = MakeContext("search");  // EmptyArgs has no "query"
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().Be("ToolDenied");
        outcome.Result.Should().Contain("query");
    }

    [Fact]
    public async Task ArgValidation_All_Required_Args_Present_Passes_Through()
    {
        var required = new Dictionary<string, IReadOnlyList<string>>
        {
            ["search"] = ["query"],
        };
        var mw = new ToolArgumentValidationMiddleware(required);
        var args = JsonDocument.Parse("{\"query\":\"hello\"}").RootElement;
        var ctx = MakeContext("search", args: args);
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "results"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().BeNull();
        outcome.Result.Should().Be("results");
    }

    [Fact]
    public async Task ArgValidation_Unknown_Tool_Passes_Through()
    {
        var required = new Dictionary<string, IReadOnlyList<string>>
        {
            ["known_tool"] = ["arg1"],
        };
        var mw = new ToolArgumentValidationMiddleware(required);
        var ctx = MakeContext("unknown_tool");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().BeNull();
    }

    // ── ToolOutputLengthGuard ────────────────────────────────────────────────

    [Fact]
    public async Task OutputLengthGuard_Short_Output_Passes_Through()
    {
        var mw = new ToolOutputLengthGuard(maxCharacters: 100);
        var ctx = MakeContext();
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "short"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().BeNull();
        outcome.Result.Should().Be("short");
    }

    [Fact]
    public async Task OutputLengthGuard_Long_Output_Returns_ToolOutputTooLarge()
    {
        var mw = new ToolOutputLengthGuard(maxCharacters: 5);
        var ctx = MakeContext("big");
        Func<Task<ToolCallOutcome>> next = () =>
            Task.FromResult(new ToolCallOutcome("c1", "this is a very long response"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().Be("ToolOutputTooLarge");
        outcome.Result.Should().Contain("big");
    }

    [Fact]
    public async Task OutputLengthGuard_Error_Outcome_Passes_Through_Regardless_Of_Length()
    {
        var mw = new ToolOutputLengthGuard(maxCharacters: 1);
        var ctx = MakeContext();
        var errorOutcome = new ToolCallOutcome("c1", new string('x', 100), "SomeError");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(errorOutcome);

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Should().Be(errorOutcome);
    }
}
