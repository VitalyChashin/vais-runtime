// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Producer seam an observability-kind <see cref="OntologyInterceptor"/> writes trajectory
/// events into. The substrate ships a <see cref="NullInterceptorTee"/> default that drops
/// events; Plan D's trajectory tee replaces the registration with a real consumer
/// (trajectory store, OTLP forwarder, induction-pipeline ingest).
/// </summary>
/// <remarks>
/// Carrying the producer seam in the substrate (rather than embedding it in a future
/// observability project) keeps Plan D additive — interceptors written today against this
/// interface keep working when the real consumer is registered, with no source change.
/// </remarks>
public interface IInterceptorTee
{
    /// <summary>
    /// Emit a single trajectory event. Implementations must be fire-and-forget from the
    /// caller's perspective — never block the interception lifecycle on tee delivery.
    /// </summary>
    ValueTask EmitAsync(InterceptorTeeEvent teeEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// One trajectory event published through <see cref="IInterceptorTee.EmitAsync"/>. Plan D
/// defines the canonical event names and payload shapes; in C1 the type is intentionally
/// open so the producer seam exists without committing to a schema.
/// </summary>
public sealed record InterceptorTeeEvent
{
    /// <summary>Event name (e.g. <c>tool.call.start</c>). Plan D specifies the catalog.</summary>
    public required string EventName { get; init; }

    /// <summary>The interception context the event was emitted from. Carries operation kind, binding version, agent context.</summary>
    public required InterceptionContext Context { get; init; }

    /// <summary>Opaque payload — schema is event-specific. May be <c>null</c> for lifecycle markers.</summary>
    public object? Payload { get; init; }
}

/// <summary>
/// Default <see cref="IInterceptorTee"/> implementation that drops every event. Registered
/// by default so observability interceptors can be written and tested against the seam
/// before Plan D's real consumer ships.
/// </summary>
public sealed class NullInterceptorTee : IInterceptorTee
{
    /// <summary>Singleton instance — the tee is stateless.</summary>
    public static IInterceptorTee Instance { get; } = new NullInterceptorTee();

    /// <inheritdoc />
    public ValueTask EmitAsync(InterceptorTeeEvent teeEvent, CancellationToken cancellationToken = default)
        => default;
}
