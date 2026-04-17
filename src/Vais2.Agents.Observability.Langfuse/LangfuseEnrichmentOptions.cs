// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Observability.Langfuse;

/// <summary>
/// Configuration for <see cref="LangfuseEnrichmentFilter"/>. All fields are optional.
/// </summary>
public sealed record LangfuseEnrichmentOptions
{
    /// <summary>
    /// Tags emitted on every enriched trace (e.g. <c>vais2-agents</c>, <c>semantic-kernel</c>).
    /// Defaults to a single <c>vais2-agents</c> tag when unset. Consumers typically add their own
    /// application name here so Langfuse UI filters pick them up.
    /// </summary>
    public IReadOnlyList<string> DefaultTags { get; init; } = new[] { "vais2-agents" };

    /// <summary>
    /// Optional extra metadata attached to every trace under <c>langfuse.trace.metadata.*</c>.
    /// Use this for static values (service name, deployment environment) that don't change per turn.
    /// </summary>
    public IReadOnlyDictionary<string, string>? StaticMetadata { get; init; }

    /// <summary>
    /// Optional anonymous-fallback user id used when <c>AgentContext.UserId</c> is null.
    /// VAIS2 ships <c>"anonymous"</c>; leave null to omit the tag entirely in that case.
    /// </summary>
    public string? AnonymousUserId { get; init; }
}
