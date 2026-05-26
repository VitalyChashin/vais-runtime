// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Filter for <see cref="IInterceptorTeeStore.QueryAsync"/>. All fields are optional; the
/// store returns events that satisfy every non-null filter (AND semantics).
/// </summary>
/// <param name="AgentId">Restrict to one agent's traces.</param>
/// <param name="RunId">Restrict to one run (typically used with <paramref name="AgentId"/>).</param>
/// <param name="ConceptName">Restrict to one tool / verb (e.g. <c>tavily_search</c>).</param>
/// <param name="Transport">Restrict to one transport (<c>north</c> | <c>south</c>).</param>
/// <param name="Since">Lower-bound timestamp (inclusive).</param>
/// <param name="Until">Upper-bound timestamp (exclusive).</param>
/// <param name="OutcomeKind">Restrict to one outcome kind (e.g. only failures).</param>
/// <param name="Limit">Maximum number of events to return. Stores default-cap if null.</param>
public sealed record TrajectoryQuery(
    string? AgentId = null,
    string? RunId = null,
    string? ConceptName = null,
    string? Transport = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    TrajectoryOutcomeKind? OutcomeKind = null,
    int? Limit = null);

/// <summary>
/// Persistence seam for the trajectory tee. Plan D ships an in-memory default
/// (<c>InMemoryInterceptorTeeStore</c>) and a Postgres-backed default
/// (<c>PostgresInterceptorTeeStore</c>); deployers can implement custom stores against this
/// seam (e.g. write through to OpenTelemetry / Langfuse / ClickHouse) without touching the
/// substrate.
/// </summary>
/// <remarks>
/// Append is fire-and-forget from the interceptor's perspective — implementations must
/// never block the interception lifecycle. Failures should be logged at warn level; do not
/// throw out of <see cref="AppendAsync"/>.
/// </remarks>
public interface IInterceptorTeeStore
{
    /// <summary>Persist a single trajectory event.</summary>
    ValueTask AppendAsync(TrajectoryEvent trajectoryEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream events matching <paramref name="query"/> in <see cref="TrajectoryEvent.Timestamp"/>-
    /// descending order (newest first). Stores SHOULD apply <paramref name="query"/>'s filters
    /// efficiently (indexed columns); the in-memory default scans the ring.
    /// </summary>
    IAsyncEnumerable<TrajectoryEvent> QueryAsync(TrajectoryQuery query, CancellationToken cancellationToken = default);
}
