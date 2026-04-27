// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.Governance;

/// <summary>
/// Persistence contract for sliding-window rate-limit counters.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>
    /// Records a new request event (with optional token count) under <paramref name="key"/> and
    /// returns the totals currently within the sliding window.
    /// </summary>
    ValueTask<(int Requests, int Tokens)> RecordAndGetAsync(
        string key,
        int tokens,
        TimeSpan window,
        CancellationToken cancellationToken = default);
}
