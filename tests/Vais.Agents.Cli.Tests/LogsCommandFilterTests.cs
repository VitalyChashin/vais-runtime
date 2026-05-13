// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class LogsCommandFilterTests
{
    [Fact]
    public void ParseKindFilter_Null_ReturnsNull()
    {
        LogsCommand.ParseKindFilter(null).Should().BeNull();
        LogsCommand.ParseKindFilter(string.Empty).Should().BeNull();
        LogsCommand.ParseKindFilter("   ").Should().BeNull();
    }

    [Fact]
    public void ParseKindFilter_SingleKind_ReturnsSet()
    {
        var filter = LogsCommand.ParseKindFilter("turn.started");

        filter.Should().NotBeNull();
        filter!.Should().ContainSingle().Which.Should().Be("turn.started");
    }

    [Fact]
    public void ParseKindFilter_CommaSeparated_StripsWhitespace()
    {
        var filter = LogsCommand.ParseKindFilter("turn.started, tool.completed ,delta");

        filter.Should().NotBeNull();
        filter!.Should().HaveCount(3).And.Contain("turn.started").And.Contain("tool.completed").And.Contain("delta");
    }

    [Fact]
    public void ParseKindFilter_CaseInsensitive()
    {
        var filter = LogsCommand.ParseKindFilter("TURN.STARTED");
        filter!.Contains("turn.started").Should().BeTrue();
    }

    [Fact]
    public void EventKindName_KnownEvents_KebabCased()
    {
        var context = new AgentContext();
        LogsCommand.EventKindName(new TurnStarted(DateTimeOffset.UtcNow, context, "hi")).Should().Be("turn.started");
        LogsCommand.EventKindName(new ToolCallStarted(DateTimeOffset.UtcNow, context, "call", "weather")).Should().Be("tool.started");
        LogsCommand.EventKindName(new CompletionDelta(DateTimeOffset.UtcNow, context, "x")).Should().Be("delta");
    }
}
