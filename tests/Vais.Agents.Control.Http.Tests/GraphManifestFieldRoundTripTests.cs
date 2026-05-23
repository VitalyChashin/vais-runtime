// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Round-trip completeness guard for <see cref="AgentGraphManifest"/> — the graph
/// analogue of <see cref="ManifestFieldRoundTripTests"/> (M1.3), but the walker
/// recurses the nested record graph (<c>GraphNode</c> → <c>GraphAgentRef</c> /
/// <c>GraphHandlerRef</c> / <c>GraphStateBindings</c> / <c>GraphNodeRetryPolicy</c>,
/// <c>GraphEdge</c>) rather than walking a flat property list.
/// </summary>
/// <remarks>
/// <para>
/// There are <b>two</b> hand-written graph serializers, both guarded here:
/// </para>
/// <list type="bullet">
///   <item><description><c>EnvelopeSerializer.Serialize(AgentGraphManifest)</c> —
///   the client/<c>vais apply</c> path (CLI parses → record → serialize → wire).</description></item>
///   <item><description><see cref="AgentGraphManifestEnvelope.Serialize"/> —
///   the server/<c>vais get -o yaml</c> path.</description></item>
/// </list>
/// <para>
/// Each field must survive both serialize paths through
/// <see cref="JsonAgentGraphManifestLoader"/> without being dropped. The abstract
/// closed hierarchies (<c>GraphEdgePredicate</c>, <c>GraphEdgeEffect</c>,
/// <c>GraphStateReducer</c>) are treated as sentinel leaves here — their per-subtype
/// round-trips live in <c>EnvelopeSerializerGraphEdgeTests</c> (client) and
/// <c>AgentGraphManifestLoaderTests</c> (server), the same split as enums →
/// <c>ManifestEnumRoundTripTests</c>.
/// </para>
/// </remarks>
public sealed class GraphManifestFieldRoundTripTests
{
    // A graph populated so that every walker-discovered field carries a distinctive,
    // non-default value. Must pass AgentGraphManifestValidator (the loader runs it).
    private static AgentGraphManifest Rich()
    {
        var schema = JsonDocument.Parse(
            "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"},\"y\":{\"type\":\"string\"}}}")
            .RootElement.Clone();

        var nodes = new GraphNode[]
        {
            new("agent-a2a", "Agent",
                Ref: new GraphAgentRef("remote-agent", Version: "2.0", A2AUrl: "https://a2a.example.com/agent")),
            new("agent-runtime", "Agent",
                Ref: new GraphAgentRef("remote-runtime-agent", RuntimeUrl: "https://runtime.example.com")),
            new("code", "Code",
                HandlerRef: new GraphHandlerRef("My.Handler.Type", "My.Assembly"),
                StateBindings: new GraphStateBindings(Input: new[] { "x" }, Output: new[] { "y" }),
                RetryPolicy: new GraphNodeRetryPolicy(3, 1.5, 2.5, 45.0)),
            new("pause", "Interrupt", InterruptReason: "needs-human"),
            new("done", "End"),
        };

        var edges = new GraphEdge[]
        {
            new("agent-a2a", "agent-runtime", Concurrent: true),
            new("agent-runtime", "code",
                When: new GraphEdgePredicate.PropertyMatcher(
                    "status", GraphPredicateOperator.Eq, JsonDocument.Parse("\"ready\"").RootElement.Clone()),
                OnTraverse: new GraphEdgeEffect.Increment("count", 2)),
            new("code", "pause"),
            new("pause", "done"),
        };

        return new AgentGraphManifest(
            Id: "rich-graph", Version: "1.0", Entry: "agent-a2a",
            Nodes: nodes, Edges: edges,
            Description: "rich graph",
            Labels: new Dictionary<string, string> { ["team"] = "platform" },
            Annotations: new Dictionary<string, string> { ["owner"] = "vais" })
        {
            StateSchema = schema,
            MaxSteps = 250,
            StateReducers = new Dictionary<string, GraphStateReducer>
            {
                ["x"] = new GraphStateReducer.Append(),
                ["y"] = new GraphStateReducer.LastWriteWins(),
            },
        };
    }

    private static GraphNode Node(AgentGraphManifest m, string id) => m.Nodes.Single(n => n.Id == id);
    private static GraphEdge Edge(AgentGraphManifest m, string from, string to) =>
        m.Edges.Single(e => e.From == from && e.To == to);

    // ── round-trip cases — one row per walker-discovered field ─────────────────

    private static object?[] Row(string path, Func<AgentGraphManifest, object?> extract, object? expected)
        => [path, extract, expected];

    public static IEnumerable<object?[]> RoundTripCases()
    {
        yield return Row("Description", m => m.Description, "rich graph");
        yield return Row("Labels", m => m.Labels!["team"], "platform");
        yield return Row("Annotations", m => m.Annotations!["owner"], "vais");
        yield return Row("StateSchema",
            m => m.StateSchema!.Value.GetProperty("properties").GetProperty("x").GetProperty("type").GetString(),
            "string");
        yield return Row("MaxSteps", m => m.MaxSteps, (object?)250);
        yield return Row("StateReducers", m => m.StateReducers!.Count, (object?)2);

        yield return Row("Nodes.Ref.Id", m => Node(m, "agent-a2a").Ref!.Id, "remote-agent");
        yield return Row("Nodes.Ref.Version", m => Node(m, "agent-a2a").Ref!.Version, "2.0");
        yield return Row("Nodes.Ref.RuntimeUrl", m => Node(m, "agent-runtime").Ref!.RuntimeUrl, "https://runtime.example.com");
        yield return Row("Nodes.Ref.A2AUrl", m => Node(m, "agent-a2a").Ref!.A2AUrl, "https://a2a.example.com/agent");

        yield return Row("Nodes.HandlerRef.TypeName", m => Node(m, "code").HandlerRef!.TypeName, "My.Handler.Type");
        yield return Row("Nodes.HandlerRef.AssemblyName", m => Node(m, "code").HandlerRef!.AssemblyName, "My.Assembly");

        yield return Row("Nodes.StateBindings.Input", m => Node(m, "code").StateBindings!.Input!.Single(), "x");
        yield return Row("Nodes.StateBindings.Output", m => Node(m, "code").StateBindings!.Output!.Single(), "y");

        yield return Row("Nodes.InterruptReason", m => Node(m, "pause").InterruptReason, "needs-human");

        yield return Row("Nodes.RetryPolicy.MaxAttempts", m => Node(m, "code").RetryPolicy!.MaxAttempts, (object?)3);
        yield return Row("Nodes.RetryPolicy.InitialBackoffSeconds", m => Node(m, "code").RetryPolicy!.InitialBackoffSeconds, (object?)1.5);
        yield return Row("Nodes.RetryPolicy.BackoffMultiplier", m => Node(m, "code").RetryPolicy!.BackoffMultiplier, (object?)2.5);
        yield return Row("Nodes.RetryPolicy.MaxBackoffSeconds", m => Node(m, "code").RetryPolicy!.MaxBackoffSeconds, (object?)45.0);

        yield return Row("Edges.When",
            m => ((GraphEdgePredicate.PropertyMatcher)Edge(m, "agent-runtime", "code").When!).Property, "status");
        yield return Row("Edges.OnTraverse",
            m => ((GraphEdgeEffect.Increment)Edge(m, "agent-runtime", "code").OnTraverse!).By, (object?)2);
        yield return Row("Edges.Concurrent", m => Edge(m, "agent-a2a", "agent-runtime").Concurrent, (object?)true);
    }

    [Theory]
    [MemberData(nameof(RoundTripCases), DisableDiscoveryEnumeration = true)]
    public async Task Field_RoundTrips(string path, Func<AgentGraphManifest, object?> extract, object? expected)
    {
        var input = Rich();

        var viaClient = (await new JsonAgentGraphManifestLoader()
            .LoadFromStringAsync(EnvelopeSerializer.Serialize(input))).Single();
        extract(viaClient).Should().Be(expected,
            because: $"{path} must survive the client EnvelopeSerializer → JsonAgentGraphManifestLoader round-trip");

        var viaServer = (await new JsonAgentGraphManifestLoader()
            .LoadFromStringAsync(AgentGraphManifestEnvelope.Serialize(input))).Single();
        extract(viaServer).Should().Be(expected,
            because: $"{path} must survive the server AgentGraphManifestEnvelope → JsonAgentGraphManifestLoader round-trip");
    }

    // ── coverage guard ─────────────────────────────────────────────────────────

    [Fact]
    public void AllGraphManifestFields_AreCovered()
    {
        var covered = new HashSet<string>(
            RoundTripCases().Select(r => (string)r[0]!), StringComparer.Ordinal);
        var discovered = ManifestRoundTripWalker.Discover(typeof(AgentGraphManifest), AlwaysSerialized);
        var uncovered = discovered.Distinct().Except(covered).OrderBy(p => p).ToList();
        uncovered.Should().BeEmpty(
            because: "every optional field in the AgentGraphManifest record graph must have a round-trip " +
                     $"case in {nameof(RoundTripCases)}() — add the missing dotted path(s)");
    }

    // Structural required scalars always written by both serializers — not at risk of silent loss.
    private static readonly IReadOnlySet<string> AlwaysSerialized = new HashSet<string>(StringComparer.Ordinal)
    {
        "Id", "Version", "Entry", "Nodes.Id", "Nodes.Kind", "Edges.From", "Edges.To",
    };
}
