// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Kind discriminator for <see cref="AgentEventSurrogate"/>. Mirrors the closed
/// <see cref="AgentEvent"/> hierarchy in Abstractions; adding a new event subtype
/// requires extending this enum and the converter in lock-step.
/// </summary>
public enum AgentEventKind
{
    /// <summary><see cref="TurnStarted"/>.</summary>
    Started = 0,
    /// <summary><see cref="TurnCompleted"/>.</summary>
    Completed = 1,
    /// <summary><see cref="TurnFailed"/>.</summary>
    Failed = 2,
}

/// <summary>
/// Orleans serialisation surrogate for the polymorphic <see cref="AgentEvent"/> hierarchy.
/// Flat shape with a discriminator + the union of all subclass fields; nullable where the
/// discriminator doesn't require a value. The Abstractions package stays Orleans-free, so
/// the [GenerateSerializer]/[RegisterConverter] pair lives here.
/// </summary>
[GenerateSerializer]
public struct AgentEventSurrogate
{
    /// <summary>Discriminator — which concrete subtype this surrogate represents.</summary>
    [Id(0)]
    public AgentEventKind Kind;

    /// <summary>UTC timestamp when the event was emitted.</summary>
    [Id(1)]
    public DateTimeOffset At;

    /// <summary>Ambient agent context.</summary>
    [Id(2)]
    public AgentContextSurrogate Context;

    /// <summary>User message — populated for <see cref="AgentEventKind.Started"/>.</summary>
    [Id(3)]
    public string? UserMessage;

    /// <summary>Assistant text — populated for <see cref="AgentEventKind.Completed"/>.</summary>
    [Id(4)]
    public string? AssistantText;

    /// <summary>Model id — populated for <see cref="AgentEventKind.Completed"/> when reported.</summary>
    [Id(5)]
    public string? ModelId;

    /// <summary>Prompt tokens — populated for <see cref="AgentEventKind.Completed"/> when reported.</summary>
    [Id(6)]
    public int? PromptTokens;

    /// <summary>Completion tokens — populated for <see cref="AgentEventKind.Completed"/> when reported.</summary>
    [Id(7)]
    public int? CompletionTokens;

    /// <summary>
    /// Wall-clock duration — populated for <see cref="AgentEventKind.Completed"/> and
    /// <see cref="AgentEventKind.Failed"/>.
    /// </summary>
    [Id(8)]
    public TimeSpan? Duration;

    /// <summary>Error type — populated for <see cref="AgentEventKind.Failed"/>.</summary>
    [Id(9)]
    public string? ErrorType;

    /// <summary>Error message — populated for <see cref="AgentEventKind.Failed"/>.</summary>
    [Id(10)]
    public string? ErrorMessage;
}

/// <summary>
/// Shared conversion helpers between <see cref="AgentEvent"/> and <see cref="AgentEventSurrogate"/>.
/// Used by the per-subclass converters below — Orleans' <see cref="IConverter{TValue, TSurrogate}"/>
/// resolves by exact <c>TValue</c> type, so the abstract base plus each concrete subtype all need
/// their own converter entry, but they share this logic.
/// </summary>
internal static class AgentEventSurrogateHelpers
{
    private static readonly AgentContextSurrogateConverter _contextConverter = new();

    public static AgentEvent FromSurrogate(in AgentEventSurrogate surrogate)
    {
        var context = _contextConverter.ConvertFromSurrogate(surrogate.Context);
        return surrogate.Kind switch
        {
            AgentEventKind.Started => new TurnStarted(
                surrogate.At,
                context,
                surrogate.UserMessage ?? string.Empty),
            AgentEventKind.Completed => new TurnCompleted(
                surrogate.At,
                context,
                surrogate.AssistantText ?? string.Empty,
                surrogate.ModelId,
                surrogate.PromptTokens,
                surrogate.CompletionTokens,
                surrogate.Duration ?? TimeSpan.Zero),
            AgentEventKind.Failed => new TurnFailed(
                surrogate.At,
                context,
                surrogate.ErrorType ?? string.Empty,
                surrogate.ErrorMessage ?? string.Empty,
                surrogate.Duration ?? TimeSpan.Zero),
            _ => throw new NotSupportedException($"Unknown AgentEventKind: {surrogate.Kind}"),
        };
    }

    public static AgentEventSurrogate ToSurrogate(in AgentEvent value)
    {
        var contextSurrogate = _contextConverter.ConvertToSurrogate(value.Context);
        return value switch
        {
            TurnStarted s => new AgentEventSurrogate
            {
                Kind = AgentEventKind.Started,
                At = s.At,
                Context = contextSurrogate,
                UserMessage = s.UserMessage,
            },
            TurnCompleted c => new AgentEventSurrogate
            {
                Kind = AgentEventKind.Completed,
                At = c.At,
                Context = contextSurrogate,
                AssistantText = c.AssistantText,
                ModelId = c.ModelId,
                PromptTokens = c.PromptTokens,
                CompletionTokens = c.CompletionTokens,
                Duration = c.Duration,
            },
            TurnFailed f => new AgentEventSurrogate
            {
                Kind = AgentEventKind.Failed,
                At = f.At,
                Context = contextSurrogate,
                ErrorType = f.ErrorType,
                ErrorMessage = f.ErrorMessage,
                Duration = f.Duration,
            },
            _ => throw new NotSupportedException($"Unknown AgentEvent subtype: {value.GetType().Name}"),
        };
    }
}

/// <summary>
/// Converter for the abstract <see cref="AgentEvent"/> base type. Orleans uses exact-type
/// dispatch for <see cref="IConverter{TValue, TSurrogate}"/>, so polymorphic sites that pass
/// events as <see cref="AgentEvent"/> (e.g. <c>IAsyncStream&lt;AgentEvent&gt;</c>) resolve
/// through this one.
/// </summary>
[RegisterConverter]
public sealed class AgentEventSurrogateConverter : IConverter<AgentEvent, AgentEventSurrogate>
{
    /// <inheritdoc />
    public AgentEvent ConvertFromSurrogate(in AgentEventSurrogate surrogate)
        => AgentEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentEventSurrogate ConvertToSurrogate(in AgentEvent value)
        => AgentEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>
/// Converter for concrete <see cref="TurnStarted"/>. Needed alongside the base-type
/// converter because Orleans resolves by exact runtime type when events are boxed
/// (e.g. through memory-stream's internal <c>List&lt;object&gt;</c>).
/// </summary>
[RegisterConverter]
public sealed class TurnStartedSurrogateConverter : IConverter<TurnStarted, AgentEventSurrogate>
{
    /// <inheritdoc />
    public TurnStarted ConvertFromSurrogate(in AgentEventSurrogate surrogate)
        => (TurnStarted)AgentEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentEventSurrogate ConvertToSurrogate(in TurnStarted value)
        => AgentEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="TurnCompleted"/>.</summary>
[RegisterConverter]
public sealed class TurnCompletedSurrogateConverter : IConverter<TurnCompleted, AgentEventSurrogate>
{
    /// <inheritdoc />
    public TurnCompleted ConvertFromSurrogate(in AgentEventSurrogate surrogate)
        => (TurnCompleted)AgentEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentEventSurrogate ConvertToSurrogate(in TurnCompleted value)
        => AgentEventSurrogateHelpers.ToSurrogate(value);
}

/// <summary>Converter for concrete <see cref="TurnFailed"/>.</summary>
[RegisterConverter]
public sealed class TurnFailedSurrogateConverter : IConverter<TurnFailed, AgentEventSurrogate>
{
    /// <inheritdoc />
    public TurnFailed ConvertFromSurrogate(in AgentEventSurrogate surrogate)
        => (TurnFailed)AgentEventSurrogateHelpers.FromSurrogate(surrogate);

    /// <inheritdoc />
    public AgentEventSurrogate ConvertToSurrogate(in TurnFailed value)
        => AgentEventSurrogateHelpers.ToSurrogate(value);
}
