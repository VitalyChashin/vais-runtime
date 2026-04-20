// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>Outcome of <see cref="IIdempotencyStore.TryBeginAsync"/>. Drives the middleware dispatch.</summary>
public enum IdempotencyBeginStatus
{
    /// <summary>First-sighting for this key; caller proceeds to the handler and reports back via <see cref="IIdempotencyStore.CompleteAsync"/> or <see cref="IIdempotencyStore.ReleaseAsync"/>.</summary>
    New = 0,

    /// <summary>Same key + same body as a previously-completed request; caller replays the cached response (carried on <see cref="IdempotencyBeginResult.CachedResponse"/>).</summary>
    Replay = 1,

    /// <summary>Same key was seen earlier with a different body. Client bug — caller surfaces 422 with <c>urn:vais-agents:idempotency-mismatch</c>.</summary>
    Mismatch = 2,

    /// <summary>Another request with the same key is currently executing (on this or another worker). Caller surfaces 409 with <c>urn:vais-agents:idempotency-in-flight</c> and a <c>Retry-After</c> header.</summary>
    InFlight = 3,
}

/// <summary>Result record returned by <see cref="IIdempotencyStore.TryBeginAsync"/>.</summary>
/// <param name="Status">Dispatch outcome.</param>
/// <param name="CachedResponse">Non-null only when <see cref="Status"/> is <see cref="IdempotencyBeginStatus.Replay"/>.</param>
/// <param name="ExistingFingerprint">Non-null only when <see cref="Status"/> is <see cref="IdempotencyBeginStatus.Mismatch"/>; lets callers surface diagnostic detail without re-querying the store.</param>
public sealed record IdempotencyBeginResult(
    IdempotencyBeginStatus Status,
    CachedResponse? CachedResponse = null,
    string? ExistingFingerprint = null);
