// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Kind discriminator for <see cref="AgentGraphEventSurrogate"/>. Mirrors the closed
/// <see cref="AgentGraphEvent"/> hierarchy in Abstractions; adding a new event subtype
/// requires extending this enum and the converter in lock-step.
/// </summary>
public enum AgentGraphEventKind
{
    /// <summary><see cref="GraphStarted"/>.</summary>
    Started = 0,
    /// <summary><see cref="NodeStarted"/>.</summary>
    NodeStarted = 1,
    /// <summary><see cref="NodeAgentInvoked"/>.</summary>
    NodeAgentInvoked = 2,
    /// <summary><see cref="NodeCompleted"/>.</summary>
    NodeCompleted = 3,
    /// <summary><see cref="EdgeTraversed"/>.</summary>
    EdgeTraversed = 4,
    /// <summary><see cref="StateUpdated"/>.</summary>
    StateUpdated = 5,
    /// <summary><see cref="GraphInterrupted"/>.</summary>
    Interrupted = 6,
    /// <summary><see cref="GraphResumed"/>.</summary>
    Resumed = 7,
    /// <summary><see cref="GraphCompleted"/>.</summary>
    Completed = 8,
    /// <summary><see cref="GraphFailed"/>.</summary>
    Failed = 9,
}

/// <summary>
/// Orleans serialisation surrogate for the polymorphic <see cref="AgentGraphEvent"/> hierarchy.
/// Flat shape with a discriminator + the union of all subclass fields; nullable where the
/// discriminator doesn't require a value. The Abstractions package stays Orleans-free, so the
/// [GenerateSerializer]/[RegisterConverter] pair lives here. Mirrors <see cref="AgentEventSurrogate"/>.
/// </summary>
[GenerateSerializer]
public struct AgentGraphEventSurrogate
{
    /// <summary>Discriminator — which concrete subtype this surrogate represents.</summary>
    [Id(0)]
    public AgentGraphEventKind Kind;

    /// <summary>UTC timestamp when the event was emitted (base field).</summary>
    [Id(1)]
    public DateTimeOffset At;

    /// <summary>Ambient agent context (base field).</summary>
    [Id(2)]
    public AgentContextSurrogate Context;

    /// <summary>Graph-run correlation id (base field).</summary>
    [Id(3)]
    public string RunId;

    /// <summary>Super-step index (base field).</summary>
    [Id(4)]
    public int SuperStep;

    /// <summary>Graph id — <see cref="AgentGraphEventKind.Started"/>.</summary>
    [Id(5)]
    public string? GraphId;

    /// <summary>Graph version — <see cref="AgentGraphEventKind.Started"/>.</summary>
    [Id(6)]
    public string? GraphVersion;

    /// <summary>Entry node id — <see cref="AgentGraphEventKind.Started"/>.</summary>
    [Id(7)]
    public string? EntryNodeId;

    /// <summary>Node id — NodeStarted / NodeAgentInvoked / NodeCompleted / Interrupted.</summary>
    [Id(8)]
    public string? NodeId;

    /// <summary>Node kind — NodeStarted / NodeCompleted.</summary>
    [Id(9)]
    public string? NodeKind;

    /// <summary>Agent id — <see cref="AgentGraphEventKind.NodeAgentInvoked"/>.</summary>
    [Id(10)]
    public string? AgentId;

    /// <summary>Agent input text — <see cref="AgentGraphEventKind.NodeAgentInvoked"/>.</summary>
    [Id(11)]
    public string? InputText;

    /// <summary>Agent output text — <see cref="AgentGraphEventKind.NodeAgentInvoked"/>.</summary>
    [Id(12)]
    public string? OutputText;

    /// <summary>Input tokens — <see cref="AgentGraphEventKind.NodeAgentInvoked"/>.</summary>
    [Id(13)]
    public int? InputTokens;

    /// <summary>Output tokens — <see cref="AgentGraphEventKind.NodeAgentInvoked"/>.</summary>
    [Id(14)]
    public int? OutputTokens;

    /// <summary>Duration — NodeCompleted / Completed / Failed.</summary>
    [Id(15)]
    public TimeSpan? Duration;

    /// <summary>Edge source — <see cref="AgentGraphEventKind.EdgeTraversed"/>.</summary>
    [Id(16)]
    public string? From;

    /// <summary>Edge target — <see cref="AgentGraphEventKind.EdgeTraversed"/>.</summary>
    [Id(17)]
    public string? To;

    /// <summary>Changed state keys — <see cref="AgentGraphEventKind.StateUpdated"/>. String arrays are natively Orleans-serialisable.</summary>
    [Id(18)]
    public string[]? ChangedKeys;

    /// <summary>Interrupt correlation id — Interrupted / Resumed.</summary>
    [Id(19)]
    public string? InterruptId;

    /// <summary>Interrupt reason — <see cref="AgentGraphEventKind.Interrupted"/>.</summary>
    [Id(20)]
    public string? Reason;

    /// <summary>
    /// JSON-serialised accumulated state at interrupt — <see cref="AgentGraphEventKind.Interrupted"/>.
    /// <see cref="JsonElement"/> is not directly Orleans-serialisable, so the dictionary round-trips
    /// through JSON. <see langword="null"/> when the orchestrator had no state available.
    /// </summary>
    [Id(21)]
    public string? CurrentStateJson;

    /// <summary>Resumed-from node id — <see cref="AgentGraphEventKind.Resumed"/>.</summary>
    [Id(22)]
    public string? ResumedFromNodeId;

    /// <summary>Final node id — <see cref="AgentGraphEventKind.Completed"/>.</summary>
    [Id(23)]
    public string? FinalNodeId;

    /// <summary>JSON-serialised final state — <see cref="AgentGraphEventKind.Completed"/> (see <see cref="CurrentStateJson"/> for the JSON rationale).</summary>
    [Id(24)]
    public string? FinalStateJson;

    /// <summary>Error type — <see cref="AgentGraphEventKind.Failed"/>.</summary>
    [Id(25)]
    public string? ErrorType;

    /// <summary>Error message (full stack for node failures) — <see cref="AgentGraphEventKind.Failed"/>.</summary>
    [Id(26)]
    public string? ErrorMessage;

    /// <summary>Failing node id — <see cref="AgentGraphEventKind.Failed"/> (P9 / ADR 016). Null for graph-level failures.</summary>
    [Id(27)]
    public string? FailedNodeId;
}

/// <summary>
/// Shared conversion helpers between <see cref="AgentGraphEvent"/> and <see cref="AgentGraphEventSurrogate"/>.
/// Orleans resolves <see cref="IConverter{TValue, TSurrogate}"/> by exact <c>TValue</c> type, so the
/// abstract base plus each concrete subtype all need their own converter entry; they share this logic.
/// </summary>
internal static class AgentGraphEventSurrogateHelpers
{
    private static readonly AgentContextSurrogateConverter _contextConverter = new();
    private static readonly JsonSerializerOptions _stateJsonOptions = new();

    internal static string? SerializeState(IReadOnlyDictionary<string, JsonElement>? state)
        => state is null || state.Count == 0 ? null : JsonSerializer.Serialize(state, _stateJsonOptions);

    internal static IReadOnlyDictionary<string, JsonElement>? ParseState(string? json)
        => string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, _stateJsonOptions);

    public static AgentGraphEvent FromSurrogate(in AgentGraphEventSurrogate s)
    {
        var ctx = _contextConverter.ConvertFromSurrogate(s.Context);
        return s.Kind switch
        {
            AgentGraphEventKind.Started => new GraphStarted(
                s.At, ctx, s.RunId, s.SuperStep,
                s.GraphId ?? string.Empty, s.GraphVersion ?? string.Empty, s.EntryNodeId ?? string.Empty),
            AgentGraphEventKind.NodeStarted => new NodeStarted(
                s.At, ctx, s.RunId, s.SuperStep, s.NodeId ?? string.Empty, s.NodeKind ?? string.Empty),
            AgentGraphEventKind.NodeAgentInvoked => new NodeAgentInvoked(
                s.At, ctx, s.RunId, s.SuperStep, s.NodeId ?? string.Empty, s.AgentId ?? string.Empty,
                s.InputText ?? string.Empty, s.OutputText ?? string.Empty, s.InputTokens ?? 0, s.OutputTokens ?? 0),
            AgentGraphEventKind.NodeCompleted => new NodeCompleted(
                s.At, ctx, s.RunId, s.SuperStep, s.NodeId ?? string.Empty, s.NodeKind ?? string.Empty,
                s.Duration ?? TimeSpan.Zero),
            AgentGraphEventKind.EdgeTraversed => new EdgeTraversed(
                s.At, ctx, s.RunId, s.SuperStep, s.From ?? string.Empty, s.To ?? string.Empty),
            AgentGraphEventKind.StateUpdated => new StateUpdated(
                s.At, ctx, s.RunId, s.SuperStep, s.ChangedKeys ?? Array.Empty<string>()),
            AgentGraphEventKind.Interrupted => new GraphInterrupted(
                s.At, ctx, s.RunId, s.SuperStep, s.NodeId ?? string.Empty, s.InterruptId ?? string.Empty, s.Reason)
            {
                CurrentState = ParseState(s.CurrentStateJson),
            },
            AgentGraphEventKind.Resumed => new GraphResumed(
                s.At, ctx, s.RunId, s.SuperStep, s.ResumedFromNodeId ?? string.Empty, s.InterruptId ?? string.Empty),
            AgentGraphEventKind.Completed => new GraphCompleted(
                s.At, ctx, s.RunId, s.SuperStep, s.FinalNodeId ?? string.Empty, s.Duration ?? TimeSpan.Zero,
                ParseState(s.FinalStateJson)),
            AgentGraphEventKind.Failed => new GraphFailed(
                s.At, ctx, s.RunId, s.SuperStep, s.ErrorType ?? string.Empty, s.ErrorMessage ?? string.Empty,
                s.Duration ?? TimeSpan.Zero, s.FailedNodeId),
            _ => throw new NotSupportedException($"Unknown AgentGraphEventKind: {s.Kind}"),
        };
    }

    public static AgentGraphEventSurrogate ToSurrogate(in AgentGraphEvent value)
    {
        var ctx = _contextConverter.ConvertToSurrogate(value.Context);
        var s = new AgentGraphEventSurrogate
        {
            At = value.At,
            Context = ctx,
            RunId = value.RunId,
            SuperStep = value.SuperStep,
        };
        switch (value)
        {
            case GraphStarted e:
                s.Kind = AgentGraphEventKind.Started;
                s.GraphId = e.GraphId;
                s.GraphVersion = e.GraphVersion;
                s.EntryNodeId = e.EntryNodeId;
                break;
            case NodeStarted e:
                s.Kind = AgentGraphEventKind.NodeStarted;
                s.NodeId = e.NodeId;
                s.NodeKind = e.NodeKind;
                break;
            case NodeAgentInvoked e:
                s.Kind = AgentGraphEventKind.NodeAgentInvoked;
                s.NodeId = e.NodeId;
                s.AgentId = e.AgentId;
                s.InputText = e.InputText;
                s.OutputText = e.OutputText;
                s.InputTokens = e.InputTokens;
                s.OutputTokens = e.OutputTokens;
                break;
            case NodeCompleted e:
                s.Kind = AgentGraphEventKind.NodeCompleted;
                s.NodeId = e.NodeId;
                s.NodeKind = e.NodeKind;
                s.Duration = e.Duration;
                break;
            case EdgeTraversed e:
                s.Kind = AgentGraphEventKind.EdgeTraversed;
                s.From = e.From;
                s.To = e.To;
                break;
            case StateUpdated e:
                s.Kind = AgentGraphEventKind.StateUpdated;
                s.ChangedKeys = e.ChangedKeys as string[] ?? e.ChangedKeys.ToArray();
                break;
            case GraphInterrupted e:
                s.Kind = AgentGraphEventKind.Interrupted;
                s.NodeId = e.NodeId;
                s.InterruptId = e.InterruptId;
                s.Reason = e.Reason;
                s.CurrentStateJson = SerializeState(e.CurrentState);
                break;
            case GraphResumed e:
                s.Kind = AgentGraphEventKind.Resumed;
                s.ResumedFromNodeId = e.ResumedFromNodeId;
                s.InterruptId = e.InterruptId;
                break;
            case GraphCompleted e:
                s.Kind = AgentGraphEventKind.Completed;
                s.FinalNodeId = e.FinalNodeId;
                s.Duration = e.Duration;
                s.FinalStateJson = SerializeState(e.FinalState);
                break;
            case GraphFailed e:
                s.Kind = AgentGraphEventKind.Failed;
                s.ErrorType = e.ErrorType;
                s.ErrorMessage = e.ErrorMessage;
                s.Duration = e.Duration;
                s.FailedNodeId = e.FailedNodeId;
                break;
            default:
                throw new NotSupportedException($"Unknown AgentGraphEvent subtype: {value.GetType().Name}");
        }
        return s;
    }
}

/// <summary>
/// Converter for the abstract <see cref="AgentGraphEvent"/> base type. Orleans uses exact-type
/// dispatch for <see cref="IConverter{TValue, TSurrogate}"/>, so polymorphic sites that pass events
/// as <see cref="AgentGraphEvent"/> (e.g. <c>IAsyncStream&lt;AgentGraphEvent&gt;</c>) resolve here.
/// </summary>
[RegisterConverter]
public sealed class AgentGraphEventSurrogateConverter : IConverter<AgentGraphEvent, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public AgentGraphEvent ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in AgentGraphEvent value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="GraphStarted"/> (needed for exact-type dispatch when boxed).</summary>
[RegisterConverter]
public sealed class GraphStartedSurrogateConverter : IConverter<GraphStarted, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public GraphStarted ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (GraphStarted)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in GraphStarted value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="NodeStarted"/>.</summary>
[RegisterConverter]
public sealed class NodeStartedSurrogateConverter : IConverter<NodeStarted, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public NodeStarted ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (NodeStarted)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in NodeStarted value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="NodeAgentInvoked"/>.</summary>
[RegisterConverter]
public sealed class NodeAgentInvokedSurrogateConverter : IConverter<NodeAgentInvoked, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public NodeAgentInvoked ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (NodeAgentInvoked)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in NodeAgentInvoked value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="NodeCompleted"/>.</summary>
[RegisterConverter]
public sealed class NodeCompletedSurrogateConverter : IConverter<NodeCompleted, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public NodeCompleted ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (NodeCompleted)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in NodeCompleted value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="EdgeTraversed"/>.</summary>
[RegisterConverter]
public sealed class EdgeTraversedSurrogateConverter : IConverter<EdgeTraversed, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public EdgeTraversed ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (EdgeTraversed)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in EdgeTraversed value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="StateUpdated"/>.</summary>
[RegisterConverter]
public sealed class StateUpdatedSurrogateConverter : IConverter<StateUpdated, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public StateUpdated ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (StateUpdated)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in StateUpdated value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="GraphInterrupted"/>.</summary>
[RegisterConverter]
public sealed class GraphInterruptedSurrogateConverter : IConverter<GraphInterrupted, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public GraphInterrupted ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (GraphInterrupted)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in GraphInterrupted value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="GraphResumed"/>.</summary>
[RegisterConverter]
public sealed class GraphResumedSurrogateConverter : IConverter<GraphResumed, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public GraphResumed ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (GraphResumed)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in GraphResumed value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="GraphCompleted"/>.</summary>
[RegisterConverter]
public sealed class GraphCompletedSurrogateConverter : IConverter<GraphCompleted, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public GraphCompleted ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (GraphCompleted)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in GraphCompleted value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="GraphFailed"/>.</summary>
[RegisterConverter]
public sealed class GraphFailedSurrogateConverter : IConverter<GraphFailed, AgentGraphEventSurrogate>
{
    /// <inheritdoc />
    public GraphFailed ConvertFromSurrogate(in AgentGraphEventSurrogate surrogate)
        => (GraphFailed)AgentGraphEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentGraphEventSurrogate ConvertToSurrogate(in GraphFailed value)
        => AgentGraphEventSurrogateHelpers.ToSurrogate(value);
}
