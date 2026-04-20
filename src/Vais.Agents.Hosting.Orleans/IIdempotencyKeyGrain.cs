// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-side surface for a single <see cref="IdempotencyKey"/> reservation.
/// One grain instance per 4-tuple scope (tenant, method, path, key); the grain
/// encodes all lifecycle transitions (<see cref="IdempotencyBeginStatus.New"/>
/// → <see cref="IdempotencyBeginStatus.Replay"/> / <see cref="IdempotencyBeginStatus.Mismatch"/>
/// / <see cref="IdempotencyBeginStatus.InFlight"/>) atomically on the grain thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grain key.</b> Composite string built from the scope 4-tuple via
/// <see cref="OrleansIdempotencyStore.BuildGrainKey"/>: each part URL-encoded
/// then joined with <c>|</c> so no component can collide with the separator.
/// </para>
/// <para>
/// <b>Single-writer guarantee.</b> Orleans serialises calls per grain — the
/// atomicity the <see cref="IIdempotencyStore.TryBeginAsync"/> contract demands
/// falls out of the grain scheduler, no additional locking required.
/// </para>
/// <para>
/// <b>Wire shape.</b> Same JSON-blob pattern as v0.8's <c>A2ATaskSurrogate</c>
/// + v0.9's <c>GraphCheckpointSurrogate</c>: the cached response is serialised
/// into the surrogate's <see cref="IdempotencyKeySurrogate.ResponseJson"/> field
/// so <see cref="CachedResponse"/> evolution doesn't require hand-synced
/// Orleans edits.
/// </para>
/// </remarks>
public interface IIdempotencyKeyGrain : IGrainWithStringKey
{
    /// <summary>Attempt to reserve this key with the given fingerprint. TTL determines how long completed entries stay valid.</summary>
    Task<IdempotencyGrainBeginResult> TryBeginAsync(string fingerprint, TimeSpan ttl);

    /// <summary>Record the cached response for a reservation. Called on 2xx / 4xx handler completion. Fingerprint is preserved from the <see cref="TryBeginAsync"/> reservation.</summary>
    Task CompleteAsync(string responseJson, TimeSpan ttl, DateTimeOffset completedAt);

    /// <summary>Release the reservation without caching a response. Called on 5xx / handler exception.</summary>
    Task ReleaseAsync();
}

/// <summary>
/// Orleans-serialisable return type for <see cref="IIdempotencyKeyGrain.TryBeginAsync"/>.
/// Carries the same information as <see cref="IdempotencyBeginResult"/> but in a
/// shape the Orleans code-generator can build a serializer for directly — the
/// contract-side record lives in <c>Vais.Agents.Control.Abstractions</c> which
/// deliberately has no Orleans dependency. <see cref="OrleansIdempotencyStore"/>
/// translates between the two.
/// </summary>
[GenerateSerializer]
public struct IdempotencyGrainBeginResult
{
    /// <summary>Dispatch status.</summary>
    [Id(0)]
    public IdempotencyBeginStatus Status;

    /// <summary>JSON-serialised <see cref="Vais.Agents.Control.CachedResponse"/>; non-null only on <see cref="IdempotencyBeginStatus.Replay"/>.</summary>
    [Id(1)]
    public string? CachedResponseJson;

    /// <summary>Fingerprint of the existing entry when the status is <see cref="IdempotencyBeginStatus.Mismatch"/>.</summary>
    [Id(2)]
    public string? ExistingFingerprint;
}

/// <summary>
/// Orleans wire-shape for a persisted idempotency entry. Carries the cached
/// response serialised as JSON plus denormalised fingerprint + state + TTL
/// fields for debugging / post-TTL eviction hooks without full deserialisation.
/// </summary>
[GenerateSerializer]
public struct IdempotencyKeySurrogate
{
    /// <summary>Composite grain key (redundant with the grain's primary key string; kept denormalised for debugging).</summary>
    [Id(0)]
    public string GrainKey;

    /// <summary>Current reservation state (<c>InFlight</c> or <c>Completed</c>).</summary>
    [Id(1)]
    public IdempotencyEntryState State;

    /// <summary>SHA-256 hash of the request body; used for mismatch detection on replay attempts.</summary>
    [Id(2)]
    public string Fingerprint;

    /// <summary>Full <see cref="CachedResponse"/> serialised via <see cref="System.Text.Json.JsonSerializer"/>. Null for <c>InFlight</c>.</summary>
    [Id(3)]
    public string? ResponseJson;

    /// <summary>Wall-clock instant at which the entry expires and is treated as missing.</summary>
    [Id(4)]
    public DateTimeOffset ExpiresAt;
}

/// <summary>Orleans-persisted state machine for <see cref="IIdempotencyKeyGrain"/> entries.</summary>
public enum IdempotencyEntryState
{
    /// <summary>Reservation made via <see cref="IIdempotencyKeyGrain.TryBeginAsync"/>; handler executing.</summary>
    InFlight = 0,

    /// <summary>Handler completed with 2xx or 4xx; cached response available for replay until TTL.</summary>
    Completed = 1,
}
