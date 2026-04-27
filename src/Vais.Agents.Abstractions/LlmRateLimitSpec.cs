// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>Rate caps applied to all LLM calls routed through a <c>LlmGatewayConfig</c>.</summary>
public sealed record LlmRateLimitSpec
{
    /// <summary>Max completion requests per minute across all agents using this config. Null = uncapped.</summary>
    public int? RequestsPerMinute { get; init; }

    /// <summary>Max tokens (prompt + completion) per minute. Null = uncapped.</summary>
    public int? TokensPerMinute { get; init; }
}
