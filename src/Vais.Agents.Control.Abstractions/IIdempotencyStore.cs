// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Dedupe store for client-sent <c>Idempotency-Key</c> values on write requests.
/// Atomically reserves a key at request entry, captures the response on the
/// handler's success path, and replays the cached response on subsequent
/// retries of the same logical operation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract shape (Stripe-style).</b> The three methods model a three-phase
/// lifecycle: <see cref="TryBeginAsync"/> at request entry returns a
/// <see cref="IdempotencyBeginResult"/> telling the middleware whether to proceed
/// (<see cref="IdempotencyBeginStatus.New"/>), replay a cached response
/// (<see cref="IdempotencyBeginStatus.Replay"/>), reject with 422 on body
/// mismatch (<see cref="IdempotencyBeginStatus.Mismatch"/>), or reject with 409
/// for a concurrent request (<see cref="IdempotencyBeginStatus.InFlight"/>).
/// <see cref="CompleteAsync"/> is called after a successful handler response
/// (2xx or 4xx) to persist the captured response; <see cref="ReleaseAsync"/> is
/// called after a 5xx / handler exception to free the reservation so the next
/// retry can proceed.
/// </para>
/// <para>
/// <b>Atomicity.</b> <see cref="TryBeginAsync"/> must be atomic — the common
/// race is two concurrent requests with the same key landing on two different
/// workers; one caller must see <see cref="IdempotencyBeginStatus.New"/> and
/// the other <see cref="IdempotencyBeginStatus.InFlight"/>. In-memory
/// implementations use <c>ConcurrentDictionary.GetOrAdd</c>; Orleans grain
/// implementations rely on the grain single-threading guarantee.
/// </para>
/// <para>
/// <b>Scoping.</b> Keys are scoped by the <see cref="IdempotencyKey"/>
/// 4-tuple — <c>(TenantId, Method, Path, Key)</c> — so cross-tenant keys never
/// collide. Anonymous (tenant-less) requests share a single null-tenant scope;
/// operators should scope by JWT in production.
/// </para>
/// <para>
/// <b>Non-HTTP consumers.</b> This contract is HTTP-shaped by origin but
/// deliberately lives in <c>Vais.Agents.Control</c> rather than
/// <c>Vais.Agents.Control.Http</c> so future interop surfaces (gRPC, A2A
/// inbound) can reuse the same dedupe story without re-inventing the
/// three-phase lifecycle.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically reserve <paramref name="key"/> for this request. Returns
    /// <see cref="IdempotencyBeginStatus.New"/> on first-sighting (caller
    /// proceeds to the handler), <see cref="IdempotencyBeginStatus.Replay"/>
    /// with the cached response when the same key + same body arrived earlier,
    /// <see cref="IdempotencyBeginStatus.Mismatch"/> when the same key arrived
    /// earlier with a different body (client bug — surface 422 to caller),
    /// or <see cref="IdempotencyBeginStatus.InFlight"/> when another request
    /// with the same key is still executing on another worker.
    /// </summary>
    /// <param name="key">4-tuple scope identifying this request.</param>
    /// <param name="fingerprint">Hash of the canonical request body; stored on
    /// first-sighting and compared against on subsequent attempts.</param>
    /// <param name="cancellationToken">Cancels the store operation.</param>
    ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyKey key,
        string fingerprint,
        CancellationToken cancellationToken);

    /// <summary>
    /// Store <paramref name="response"/> as the cached reply for
    /// <paramref name="key"/>'s reservation. Called after the handler completes
    /// with a 2xx or 4xx status. Subsequent <see cref="TryBeginAsync"/> calls
    /// with the same key + matching fingerprint will return
    /// <see cref="IdempotencyBeginStatus.Replay"/> carrying this response until
    /// the TTL expires.
    /// </summary>
    ValueTask CompleteAsync(
        IdempotencyKey key,
        CachedResponse response,
        CancellationToken cancellationToken);

    /// <summary>
    /// Release the reservation for <paramref name="key"/> without caching a
    /// response. Called when the handler threw or returned 5xx — transient
    /// failures should not lock the client out for 24h. Subsequent
    /// <see cref="TryBeginAsync"/> calls start fresh.
    /// </summary>
    ValueTask ReleaseAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken);
}
