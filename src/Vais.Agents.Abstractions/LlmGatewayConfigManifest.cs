// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative specification for a named LLM gateway pipeline. Stored in
/// <see cref="ILlmGatewayConfigRegistry"/>; referenced from
/// <see cref="AgentManifest.LlmGatewayRef"/> at agent activation time.
/// </summary>
/// <param name="Id">Stable identifier, unique within the registry namespace.</param>
/// <param name="Version">Immutable version tag. Updates create a new version.</param>
/// <param name="Middleware">
/// Ordered list of middleware layers. Execution order: index 0 = outermost interceptor.
/// Each entry is resolved at activation time via <see cref="ILlmGatewayMiddlewareFactory"/>.
/// </param>
/// <param name="Description">Human-readable description.</param>
/// <param name="Labels">Key/value metadata for filtering.</param>
public sealed record LlmGatewayConfigManifest(
    string Id,
    string Version,
    IReadOnlyList<GatewayMiddlewareSpec> Middleware,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>Optional rate-limit caps applied at the gateway level.</summary>
    public LlmRateLimitSpec? RateLimit { get; init; }

    /// <summary>Free-form operator-visible metadata.</summary>
    public IReadOnlyDictionary<string, string>? Annotations { get; init; }
}

/// <summary>Stable identity reference to a registered <see cref="LlmGatewayConfigManifest"/>.</summary>
public sealed record LlmGatewayConfigHandle(string Id, string Version);

/// <summary>Runtime status snapshot returned by <c>ILlmGatewayConfigLifecycleManager.QueryAsync</c>.</summary>
public sealed record LlmGatewayConfigStatus(LlmGatewayConfigHandle Handle, DateTimeOffset RegisteredAt);
