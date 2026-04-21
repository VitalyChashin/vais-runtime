// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Cli;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class GraphLogsCommandFilterTests
{
    private static readonly AgentContext Ctx = new();
    private static readonly DateTimeOffset T = new(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseKindFilter_Null_ReturnsNull()
    {
        GraphLogsCommand.ParseKindFilter(null).Should().BeNull();
        GraphLogsCommand.ParseKindFilter(string.Empty).Should().BeNull();
        GraphLogsCommand.ParseKindFilter("   ").Should().BeNull();
    }

    [Fact]
    public void ParseKindFilter_CommaSeparated_StripsWhitespace()
    {
        var filter = GraphLogsCommand.ParseKindFilter("graph.started, node.completed , edge.traversed");
        filter.Should().HaveCount(3).And.Contain("graph.started").And.Contain("node.completed").And.Contain("edge.traversed");
    }

    [Fact]
    public void ParseKindFilter_CaseInsensitive()
    {
        var filter = GraphLogsCommand.ParseKindFilter("GRAPH.STARTED");
        filter!.Contains("graph.started").Should().BeTrue();
    }

    [Fact]
    public void EventKindName_KnownSubtypes_MatchExpected()
    {
        GraphEventRenderer.EventKindName(new GraphStarted(T, Ctx, "r1", 0, "g", "1.0", "start")).Should().Be("graph.started");
        GraphEventRenderer.EventKindName(new GraphCompleted(T, Ctx, "r1", 0, "end", TimeSpan.Zero)).Should().Be("graph.completed");
        GraphEventRenderer.EventKindName(new GraphInterrupted(T, Ctx, "r1", 0, "n", "i", null)).Should().Be("graph.interrupted");
    }
}
