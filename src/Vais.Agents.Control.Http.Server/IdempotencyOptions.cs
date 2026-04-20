// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Configuration knobs for the idempotency middleware + store. Registered via
/// <see cref="AgentControlPlaneServiceCollectionExtensions.AddAgentControlPlaneIdempotency"/>;
/// tuned via <c>IOptions&lt;IdempotencyOptions&gt;</c> in the standard
/// <see cref="Microsoft.Extensions.Options"/> style.
/// </summary>
public sealed class IdempotencyOptions
{
    /// <summary>
    /// How long completed entries stay in the store before eviction. Default
    /// <c>24h</c> matches Stripe's published idempotency retention window.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How often the in-memory store scans for expired entries. Default
    /// <c>5 minutes</c>. Set to <see cref="TimeSpan.Zero"/> to disable the
    /// background timer entirely (manual eviction only).
    /// </summary>
    public TimeSpan EvictionInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum length of a client-supplied <c>Idempotency-Key</c> header value.
    /// Requests exceeding this are rejected with <c>400 Bad Request</c> before
    /// the handler runs. Matches Stripe's 255-char cap by default.
    /// </summary>
    public int MaxKeyLength { get; set; } = 255;

    /// <summary>
    /// Additional path prefixes (beyond <c>/healthz</c> and <c>/readyz</c>) that
    /// should bypass the idempotency middleware. Useful when consumers mount
    /// streaming or long-polling endpoints on the same prefix as the control
    /// plane and want to exclude them.
    /// </summary>
    public IList<string> PathExclusions { get; } = new List<string>();

    /// <summary>
    /// When <c>true</c> (default), the middleware skips <c>GET</c>, <c>HEAD</c>,
    /// and <c>OPTIONS</c> requests — they're naturally idempotent and shouldn't
    /// be deduped. Set to <c>false</c> only if you have an unusual reason to
    /// cache read responses.
    /// </summary>
    public bool IncludeGetsInExclusion { get; set; } = true;
}
