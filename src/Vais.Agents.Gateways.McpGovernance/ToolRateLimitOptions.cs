// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.McpGovernance;

/// <summary>Options for <see cref="ToolRateLimitMiddleware"/>.</summary>
public sealed class ToolRateLimitOptions
{
    /// <summary>Maximum number of tool calls allowed per <see cref="Window"/> per key. Default: 100.</summary>
    public int MaxRequestsPerWindow { get; init; } = 100;

    /// <summary>The sliding window duration. Default: one minute.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
}
