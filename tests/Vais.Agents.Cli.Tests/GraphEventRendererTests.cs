// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents;
using Vais.Agents.Cli;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class GraphEventRendererTests
{
    private static readonly AgentContext Ctx = new();
    private static readonly DateTimeOffset T = new(2026, 4, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EventKindName_AllKnownSubtypes_ReturnKebabCaseName()
    {
        GraphEventRenderer.EventKindName(new GraphStarted(T, Ctx, "r1", 0, "g", "1.0", "start")).Should().Be("graph.started");
        GraphEventRenderer.EventKindName(new NodeStarted(T, Ctx, "r1", 0, "n1", "Agent")).Should().Be("node.started");
        GraphEventRenderer.EventKindName(new NodeCompleted(T, Ctx, "r1", 0, "n1", "Agent", TimeSpan.Zero)).Should().Be("node.completed");
        GraphEventRenderer.EventKindName(new EdgeTraversed(T, Ctx, "r1", 0, "n1", "n2")).Should().Be("edge.traversed");
        GraphEventRenderer.EventKindName(new StateUpdated(T, Ctx, "r1", 0, new[] { "k" })).Should().Be("state.updated");
        GraphEventRenderer.EventKindName(new GraphInterrupted(T, Ctx, "r1", 0, "pause", "int-1", null)).Should().Be("graph.interrupted");
        GraphEventRenderer.EventKindName(new GraphResumed(T, Ctx, "r1", 0, "pause", "int-1")).Should().Be("graph.resumed");
        GraphEventRenderer.EventKindName(new GraphCompleted(T, Ctx, "r1", 0, "end", TimeSpan.Zero)).Should().Be("graph.completed");
        GraphEventRenderer.EventKindName(new GraphFailed(T, Ctx, "r1", 0, "Exception", "oops", TimeSpan.Zero)).Should().Be("graph.failed");
    }
}
