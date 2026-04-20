// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Snapshot of an HTTP response captured at the moment the handler completed.
/// Replayed verbatim on matching-fingerprint retries; carries enough state
/// (status, content-type, body) to reconstruct a valid response downstream.
/// </summary>
/// <param name="StatusCode">HTTP status code the handler returned.</param>
/// <param name="ContentType">Value of the <c>Content-Type</c> response header, or a reasonable default (e.g. <c>application/octet-stream</c>) when absent.</param>
/// <param name="Body">UTF-8 (or binary) response body bytes, captured post-serialisation.</param>
/// <param name="CompletedAt">Instant the handler finished. Used to compute TTL expiry (<c>CompletedAt + TTL</c>).</param>
/// <remarks>
/// <para>
/// v0.11 captures <see cref="StatusCode"/>, <see cref="ContentType"/>, and
/// <see cref="Body"/> only — no custom response headers beyond content-type.
/// Matches Stripe's minimal replay set. Consumers who need extra header replay
/// (e.g. <c>X-Request-Id</c>) can extend in a follow-up; the record is
/// evolution-friendly by construction.
/// </para>
/// </remarks>
public sealed record CachedResponse(
    int StatusCode,
    string ContentType,
    byte[] Body,
    DateTimeOffset CompletedAt);
