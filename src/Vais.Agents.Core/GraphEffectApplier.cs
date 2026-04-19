// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// Applies <see cref="GraphEdgeEffect"/> mutations to graph state. Internal helper;
/// returns the set of state keys that changed so the orchestrator can emit a
/// <see cref="StateUpdated"/> event.
/// </summary>
internal static class GraphEffectApplier
{
    public static async ValueTask<IReadOnlyList<string>> ApplyAsync(
        GraphEdgeEffect? effect,
        IDictionary<string, JsonElement> state,
        Func<GraphHandlerRef, IGraphEdgeEffect>? handlerResolver,
        CancellationToken cancellationToken)
    {
        if (effect is null)
        {
            return Array.Empty<string>();
        }

        switch (effect)
        {
            case GraphEdgeEffect.Set s:
                state[s.Property] = s.Value;
                return new[] { s.Property };

            case GraphEdgeEffect.Increment inc:
            {
                var current = state.TryGetValue(inc.Property, out var existing) && existing.ValueKind == JsonValueKind.Number
                    ? existing.GetInt32()
                    : 0;
                state[inc.Property] = JsonSerializer.SerializeToElement(current + inc.By);
                return new[] { inc.Property };
            }

            case GraphEdgeEffect.Append app:
            {
                var existing = state.TryGetValue(app.Property, out var current) && current.ValueKind == JsonValueKind.Array
                    ? current.EnumerateArray().ToList()
                    : new List<JsonElement>();
                existing.Add(app.Value);
                state[app.Property] = JsonSerializer.SerializeToElement(existing);
                return new[] { app.Property };
            }

            case GraphEdgeEffect.HandlerRef h:
            {
                if (handlerResolver is null)
                {
                    throw new InvalidOperationException(
                        $"Effect references handler '{h.Handler.TypeName}' but no resolver was supplied.");
                }
                var handler = handlerResolver(h.Handler);
                var before = state.Keys.ToHashSet(StringComparer.Ordinal);
                await handler.ApplyAsync(state, cancellationToken).ConfigureAwait(false);
                // Diff the keys — handler-driven effects don't declare which keys changed,
                // so we scan. Not ideal for perf; fine for v0.9 scope.
                var changed = new List<string>();
                foreach (var key in state.Keys)
                {
                    if (!before.Contains(key))
                    {
                        changed.Add(key);
                    }
                }
                // We can't detect value-only changes without stashing the original values —
                // future pillar can add before/after comparison if the cost matters.
                return changed;
            }

            default:
                throw new NotSupportedException($"Unknown effect subtype '{effect.GetType().Name}'.");
        }
    }
}
