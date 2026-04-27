// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.Governance;

/// <summary>
/// Options for <see cref="LlmRateLimitMiddleware"/>. At least one of
/// <see cref="MaxTokensPerWindow"/> or <see cref="MaxRequestsPerWindow"/> must be set.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Maximum total prompt tokens allowed per <see cref="Window"/>. When <see langword="null"/>, token limits are not enforced.
    /// </summary>
    public int? MaxTokensPerWindow { get; init; }

    /// <summary>
    /// Maximum number of requests (LLM calls) allowed per <see cref="Window"/>. When <see langword="null"/>, request limits are not enforced.
    /// </summary>
    public int? MaxRequestsPerWindow { get; init; }

    /// <summary>
    /// The sliding window duration. Defaults to one minute.
    /// </summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}
