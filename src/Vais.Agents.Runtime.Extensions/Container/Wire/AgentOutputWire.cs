// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions.Container.Wire;

/// <summary>
/// Canonical JSON shape for <c>agentOutput</c> context sent to <c>/handlers/&lt;id&gt;/pre</c>.
/// </summary>
internal sealed record AgentOutputPreRequest(
    string CallId,
    AgentOutputContextWire Context);

internal sealed record AgentOutputContextWire(
    string AgentId,
    string RunId,
    string? SessionId,
    int? OutputTokens,
    int? InputTokens);

/// <summary>
/// Canonical JSON shape sent to <c>/handlers/&lt;id&gt;/post</c> for agentOutput seam.
/// </summary>
internal sealed record AgentOutputPostRequest(
    string CallId,
    string? ContinuationToken);
