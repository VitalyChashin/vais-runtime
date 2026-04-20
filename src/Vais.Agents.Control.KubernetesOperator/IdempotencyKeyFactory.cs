// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Constructs <c>Idempotency-Key</c> header values for operator → control
/// plane calls. The v0.11 idempotency middleware de-duplicates repeated
/// verbs sharing the same key within the 24-hour TTL, so reconcile
/// retries after partial failure don't re-dispatch to the runtime.
/// </summary>
/// <remarks>
/// Composition is <c>{uid}:{generation}:{verb}</c>. The UID is stable
/// across a CR's lifetime; generation increments on every spec change;
/// verb disambiguates Create vs. Update vs. Evict so reconcile passes
/// that do two verbs back-to-back each get a fresh key.
/// </remarks>
internal static class IdempotencyKeyFactory
{
    /// <summary>Verb constant — passed to <see cref="Build"/> for create operations.</summary>
    public const string CreateVerb = "create";

    /// <summary>Verb constant — passed to <see cref="Build"/> for update operations.</summary>
    public const string UpdateVerb = "update";

    /// <summary>Verb constant — passed to <see cref="Build"/> for evict operations.</summary>
    public const string EvictVerb = "evict";

    /// <summary>
    /// Compose an idempotency key. Throws if <paramref name="uid"/> is
    /// null or empty — every CR has a UID assigned by the API server on
    /// creation, so missing uid indicates a bug upstream of the caller.
    /// </summary>
    public static string Build(string uid, long generation, string verb)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        return $"{uid}:{generation}:{verb}";
    }
}
