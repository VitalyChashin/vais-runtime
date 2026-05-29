// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.ScriptRuntime.Tests;

/// <summary>CM-1 — the raw MCP-derived JS API generator.</summary>
public sealed class RawMcpClientGeneratorTests
{
    [Fact]
    public void Generate_EmitsQuotedKeyedFunctions_DocComment_AndArgNames()
    {
        var js = new RawMcpClientGenerator().Generate(
        [
            new FakeTool("get_orders", "Fetch orders for a customer.",
                "{\"type\":\"object\",\"properties\":{\"customerId\":{\"type\":\"string\"}}}"),
        ]);

        js.Should().Contain("var tools = {");
        js.Should().Contain("\"get_orders\": function (args)");
        js.Should().Contain("__callTool(\"\", \"get_orders\", JSON.stringify(args === undefined ? {} : args))");
        js.Should().Contain("Fetch orders for a customer.");
        js.Should().Contain("customerId");
    }

    [Fact]
    public void Generate_NoTools_StillEmitsToolsObject()
    {
        var js = new RawMcpClientGenerator().Generate([]);

        js.Should().Contain("no tools available");
        js.Should().Contain("var tools = {");
        js.Should().Contain("};");
    }

    [Fact]
    public void Generate_MultilineDescription_StaysOnOneCommentLine()
    {
        var js = new RawMcpClientGenerator().Generate(
            [new FakeTool("t", "line one\nline two")]);

        // The comment line for the tool must not contain a raw newline from the description.
        var toolLine = js.Split('\n').Single(l => l.Contains("tools[\"t\"]"));
        toolLine.Should().Contain("line one line two");
    }
}
