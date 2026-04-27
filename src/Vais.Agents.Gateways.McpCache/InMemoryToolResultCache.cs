// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json;

namespace Vais.Agents.Gateways.McpCache;

/// <summary>
/// In-memory implementation of <see cref="IToolResultCache"/>. Suitable for single-process
/// deployments. Thread-safe.
/// </summary>
/// <remarks>
/// Key strategy: tool name + <see cref="JsonElement.ToString()"/> (normalized JSON, no whitespace).
/// Property order is as emitted by the LLM — not sorted across LLM output variations.
/// </remarks>
public sealed class InMemoryToolResultCache : IToolResultCache
{
    private readonly ConcurrentDictionary<string, ToolCallOutcome> _store = new();

    /// <inheritdoc/>
    public ValueTask<ToolCallOutcome?> TryGetAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var key = BuildKey(toolName, arguments);
        return ValueTask.FromResult(_store.TryGetValue(key, out var v) ? v : (ToolCallOutcome?)null);
    }

    /// <inheritdoc/>
    public ValueTask SetAsync(
        string toolName, JsonElement arguments, ToolCallOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        _store[BuildKey(toolName, arguments)] = outcome;
        return ValueTask.CompletedTask;
    }

    private static string BuildKey(string toolName, JsonElement arguments)
        => $"{toolName}:{arguments}";
}
