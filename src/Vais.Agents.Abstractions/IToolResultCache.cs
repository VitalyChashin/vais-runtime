// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Deterministic tool result cache. Used by <c>ToolResultCacheMiddleware</c> to
/// short-circuit repeated calls with identical arguments.
/// </summary>
/// <remarks>
/// Key strategy: tool name + stable JSON serialization of arguments.
/// Key space is entirely separate from the semantic LLM cache
/// (<c>ISemanticCacheStore</c>) — different invalidation model and TTL semantics.
/// Implementations must be thread-safe.
/// Opt-out (non-deterministic tools such as <c>get_current_time</c>) is handled
/// by the middleware via a configurable exclude list, not by this interface.
/// </remarks>
public interface IToolResultCache
{
    /// <summary>Returns the cached outcome for the given tool + arguments, or <see langword="null"/> on miss.</summary>
    ValueTask<ToolCallOutcome?> TryGetAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Stores an outcome. Existing entries for the same key are overwritten.</summary>
    ValueTask SetAsync(
        string toolName,
        JsonElement arguments,
        ToolCallOutcome outcome,
        CancellationToken cancellationToken = default);
}
