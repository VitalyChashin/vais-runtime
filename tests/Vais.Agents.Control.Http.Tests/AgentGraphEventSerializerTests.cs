// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Unit tests for <see cref="AgentGraphEventSerializer"/>. Verifies that every known
/// <see cref="AgentGraphEvent"/> subtype maps to the correct SSE event name and that
/// the emitted JSON is valid and carries expected fields. Also asserts that an unknown
/// subtype throws <see cref="ArgumentException"/>.
/// </summary>
public sealed class AgentGraphEventSerializerTests
{
    private static readonly DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AgentContext _ctx = AgentContext.Empty;
    private const string RunId = "run-abc";

    // ---- 1: GraphStarted → "graph.started", JSON contains GraphId ----

    [Fact]
    public void GraphStarted_Yields_Correct_EventName_And_GraphId()
    {
        var evt = new GraphStarted(_at, _ctx, RunId, 0, "graph-42", "1.0", "entry-node");

        var (eventName, dataJson) = AgentGraphEventSerializer.Serialize(evt);

        eventName.Should().Be("graph.started");
        var doc = JsonDocument.Parse(dataJson);
        doc.RootElement.GetProperty("graphId").GetString().Should().Be("graph-42");
    }

    // ---- 2: NodeStarted → "node.started", JSON contains NodeId ----

    [Fact]
    public void NodeStarted_Yields_Correct_EventName_And_NodeId()
    {
        var evt = new NodeStarted(_at, _ctx, RunId, 1, "node-7", "LlmNode");

        var (eventName, dataJson) = AgentGraphEventSerializer.Serialize(evt);

        eventName.Should().Be("node.started");
        var doc = JsonDocument.Parse(dataJson);
        doc.RootElement.GetProperty("nodeId").GetString().Should().Be("node-7");
    }

    // ---- 3: NodeCompleted → "node.completed", JSON contains Duration ----

    [Fact]
    public void NodeCompleted_Yields_Correct_EventName_And_Duration()
    {
        var duration = TimeSpan.FromMilliseconds(250);
        var evt = new NodeCompleted(_at, _ctx, RunId, 1, "node-7", "LlmNode", duration);

        var (eventName, dataJson) = AgentGraphEventSerializer.Serialize(evt);

        eventName.Should().Be("node.completed");
        var doc = JsonDocument.Parse(dataJson);
        // Duration serialised as ISO 8601 by System.Text.Json web defaults.
        doc.RootElement.TryGetProperty("duration", out _).Should().BeTrue();
    }

    // ---- 4: GraphInterrupted → "graph.interrupted", JSON contains InterruptId ----

    [Fact]
    public void GraphInterrupted_Yields_Correct_EventName_And_InterruptId()
    {
        var evt = new GraphInterrupted(_at, _ctx, RunId, 2, "pause-node", "interrupt-xyz", "awaiting user input");

        var (eventName, dataJson) = AgentGraphEventSerializer.Serialize(evt);

        eventName.Should().Be("graph.interrupted");
        var doc = JsonDocument.Parse(dataJson);
        doc.RootElement.GetProperty("interruptId").GetString().Should().Be("interrupt-xyz");
    }

    // ---- 5: GraphCompleted → "graph.completed", JSON is valid ----

    [Fact]
    public void GraphCompleted_Yields_Correct_EventName_And_Valid_Json()
    {
        var evt = new GraphCompleted(_at, _ctx, RunId, 5, "end-node", TimeSpan.FromSeconds(3));

        var (eventName, dataJson) = AgentGraphEventSerializer.Serialize(evt);

        eventName.Should().Be("graph.completed");
        // Must parse without throwing.
        var act = () => JsonDocument.Parse(dataJson);
        act.Should().NotThrow();
    }

    // ---- 7: GraphFailed → "graph.failed", JSON contains FailedNodeId (P9 / ADR 016) ----

    [Fact]
    public void GraphFailed_Yields_Correct_EventName_And_FailedNodeId()
    {
        var evt = new GraphFailed(_at, _ctx, RunId, 3, "InvalidOperationException", "boom",
            TimeSpan.FromSeconds(1), FailedNodeId: "bad-node");

        var (eventName, dataJson) = AgentGraphEventSerializer.Serialize(evt);

        eventName.Should().Be("graph.failed");
        var doc = JsonDocument.Parse(dataJson);
        doc.RootElement.GetProperty("failedNodeId").GetString().Should().Be("bad-node");
    }

    // ---- 8: Legacy graph.failed payload without failedNodeId deserialises to null (back-compat) ----

    [Fact]
    public void GraphFailed_LegacyPayload_Without_FailedNodeId_Deserialises_To_Null()
    {
        // A pre-P9 SSE payload carries no failedNodeId field; back-compat requires it to
        // round-trip to null rather than fail deserialisation.
        const string legacyJson =
            """{"at":"2026-01-01T00:00:00+00:00","runId":"run-abc","superStep":2,"errorType":"SomeError","errorMessage":"older payload","duration":"00:00:01"}""";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var evt = JsonSerializer.Deserialize<GraphFailed>(legacyJson, options);

        evt.Should().NotBeNull();
        evt!.FailedNodeId.Should().BeNull();
        evt.ErrorType.Should().Be("SomeError");
    }

    // ---- 6: Unknown subtype throws ArgumentException ----

    [Fact]
    public void Unknown_Subtype_Throws_ArgumentException()
    {
        var unknown = new UnknownGraphEvent(_at, _ctx, RunId, 0);

        var act = () => AgentGraphEventSerializer.Serialize(unknown);

        act.Should().Throw<ArgumentException>()
           .WithParameterName("evt");
    }

    /// <summary>
    /// A stand-in subtype that is NOT part of the closed hierarchy — used to exercise
    /// the default branch of the serializer switch.
    /// </summary>
    private sealed record UnknownGraphEvent(
        DateTimeOffset At,
        AgentContext Context,
        string RunId,
        int SuperStep)
        : AgentGraphEvent(At, Context, RunId, SuperStep);
}
