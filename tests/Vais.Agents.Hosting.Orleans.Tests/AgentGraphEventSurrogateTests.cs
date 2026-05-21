// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// Pure round-trip tests for <see cref="AgentGraphEventSurrogate"/> via the public
/// <see cref="AgentGraphEventSurrogateConverter"/> — no Orleans cluster required. Verifies every
/// <see cref="AgentGraphEvent"/> subtype's fields survive surrogate conversion, including the
/// <see cref="JsonElement"/> state dictionaries and <c>FailedNodeId</c> / <c>ChangedKeys</c>.
/// </summary>
public sealed class AgentGraphEventSurrogateTests
{
    private static readonly AgentGraphEventSurrogateConverter _conv = new();
    private static readonly DateTimeOffset _at = new(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly AgentContext _ctx = new(UserId: "u", AgentName: "a") { RunId = "run-1" };

    private static T RoundTrip<T>(T evt) where T : AgentGraphEvent
        => (T)_conv.ConvertFromSurrogate(_conv.ConvertToSurrogate(evt));

    [Fact]
    public void GraphStarted_RoundTrips()
    {
        var back = RoundTrip(new GraphStarted(_at, _ctx, "run-1", 0, "graph-1", "1.0", "entry"));
        back.GraphId.Should().Be("graph-1");
        back.GraphVersion.Should().Be("1.0");
        back.EntryNodeId.Should().Be("entry");
        back.RunId.Should().Be("run-1");
        back.Context.AgentName.Should().Be("a");
    }

    [Fact]
    public void NodeStarted_RoundTrips()
    {
        var back = RoundTrip(new NodeStarted(_at, _ctx, "run-1", 1, "node-7", "Agent"));
        back.NodeId.Should().Be("node-7");
        back.NodeKind.Should().Be("Agent");
        back.SuperStep.Should().Be(1);
    }

    [Fact]
    public void NodeAgentInvoked_RoundTrips()
    {
        var back = RoundTrip(new NodeAgentInvoked(_at, _ctx, "run-1", 2, "node-7", "agent-x", "in", "out", 11, 22));
        back.NodeId.Should().Be("node-7");
        back.AgentId.Should().Be("agent-x");
        back.InputText.Should().Be("in");
        back.OutputText.Should().Be("out");
        back.InputTokens.Should().Be(11);
        back.OutputTokens.Should().Be(22);
    }

    [Fact]
    public void NodeCompleted_RoundTrips()
    {
        var back = RoundTrip(new NodeCompleted(_at, _ctx, "run-1", 3, "node-7", "Agent", TimeSpan.FromMilliseconds(250)));
        back.NodeId.Should().Be("node-7");
        back.NodeKind.Should().Be("Agent");
        back.Duration.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void EdgeTraversed_RoundTrips()
    {
        var back = RoundTrip(new EdgeTraversed(_at, _ctx, "run-1", 4, "a", "b"));
        back.From.Should().Be("a");
        back.To.Should().Be("b");
    }

    [Fact]
    public void StateUpdated_RoundTrips_ChangedKeys()
    {
        var back = RoundTrip(new StateUpdated(_at, _ctx, "run-1", 5, new[] { "k1", "k2" }));
        back.ChangedKeys.Should().Equal("k1", "k2");
    }

    [Fact]
    public void GraphInterrupted_RoundTrips_With_CurrentState()
    {
        var state = new Dictionary<string, JsonElement>
        {
            ["draft"] = JsonSerializer.SerializeToElement("hello"),
            ["count"] = JsonSerializer.SerializeToElement(3),
        };
        var evt = new GraphInterrupted(_at, _ctx, "run-1", 6, "wait", "intr-1", "needs approval")
        {
            CurrentState = state,
        };

        var back = RoundTrip(evt);
        back.NodeId.Should().Be("wait");
        back.InterruptId.Should().Be("intr-1");
        back.Reason.Should().Be("needs approval");
        back.CurrentState.Should().NotBeNull();
        back.CurrentState!["draft"].GetString().Should().Be("hello");
        back.CurrentState["count"].GetInt32().Should().Be(3);
    }

    [Fact]
    public void GraphInterrupted_RoundTrips_With_Null_State()
    {
        var back = RoundTrip(new GraphInterrupted(_at, _ctx, "run-1", 6, "wait", "intr-1", Reason: null));
        back.Reason.Should().BeNull();
        back.CurrentState.Should().BeNull();
    }

    [Fact]
    public void GraphResumed_RoundTrips()
    {
        var back = RoundTrip(new GraphResumed(_at, _ctx, "run-1", 7, "wait", "intr-1"));
        back.ResumedFromNodeId.Should().Be("wait");
        back.InterruptId.Should().Be("intr-1");
    }

    [Fact]
    public void GraphCompleted_RoundTrips_With_FinalState()
    {
        var state = new Dictionary<string, JsonElement>
        {
            ["answer"] = JsonSerializer.SerializeToElement(42),
        };
        var back = RoundTrip(new GraphCompleted(_at, _ctx, "run-1", 8, "end", TimeSpan.FromSeconds(2), state));
        back.FinalNodeId.Should().Be("end");
        back.Duration.Should().Be(TimeSpan.FromSeconds(2));
        back.FinalState.Should().NotBeNull();
        back.FinalState!["answer"].GetInt32().Should().Be(42);
    }

    [Fact]
    public void GraphFailed_RoundTrips_With_FailedNodeId()
    {
        var back = RoundTrip(new GraphFailed(_at, _ctx, "run-1", 9, "InvalidOperationException",
            "System.InvalidOperationException: boom\n   at X", TimeSpan.FromMilliseconds(5), FailedNodeId: "node-7"));
        back.ErrorType.Should().Be("InvalidOperationException");
        back.ErrorMessage.Should().Contain("boom");
        back.FailedNodeId.Should().Be("node-7");
    }

    [Fact]
    public void GraphFailed_RoundTrips_With_Null_FailedNodeId()
    {
        var back = RoundTrip(new GraphFailed(_at, _ctx, "run-1", 9, "WorkflowError", "oops", TimeSpan.Zero));
        back.FailedNodeId.Should().BeNull();
    }
}
