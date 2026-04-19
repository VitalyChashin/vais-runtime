// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// Default state-reduction strategies used by <see cref="InProcessGraphOrchestrator"/>
/// when merging a node's output bindings back into graph state. v0.9 ships two:
/// last-write-wins (the default) and <see cref="WellKnownKey.Messages"/>-append.
/// Custom reducers arrive in a future pillar via a <see cref="GraphEdgeEffect.HandlerRef"/>-
/// style escape hatch.
/// </summary>
public static class GraphStateReducers
{
    /// <summary>Well-known state keys the runtime treats with non-default reducers.</summary>
    public static class WellKnownKey
    {
        /// <summary>
        /// Graph-wide chat history. Arrays of <see cref="ChatTurn"/>-shaped JSON objects.
        /// When a node writes to this key, the runtime appends to the existing array
        /// rather than overwriting (<see cref="AppendMessages"/>).
        /// </summary>
        public const string Messages = "messages";
    }

    /// <summary>
    /// Last-write-wins reducer. Called for every key except <see cref="WellKnownKey.Messages"/>.
    /// </summary>
    public static JsonElement LastWriteWins(JsonElement existing, JsonElement incoming) => incoming;

    /// <summary>
    /// Append reducer for array-valued state. If both sides are arrays, concatenates
    /// them; if one side is non-array, falls through to <see cref="LastWriteWins"/>.
    /// Used by <see cref="WellKnownKey.Messages"/> so multi-node graphs accumulate
    /// chat history correctly.
    /// </summary>
    public static JsonElement AppendMessages(JsonElement existing, JsonElement incoming)
    {
        if (existing.ValueKind != JsonValueKind.Array || incoming.ValueKind != JsonValueKind.Array)
        {
            return incoming;
        }
        var combined = new List<JsonElement>(existing.GetArrayLength() + incoming.GetArrayLength());
        foreach (var e in existing.EnumerateArray()) combined.Add(e);
        foreach (var i in incoming.EnumerateArray()) combined.Add(i);
        return JsonSerializer.SerializeToElement(combined);
    }

    /// <summary>
    /// Merge <paramref name="incoming"/> into <paramref name="state"/> using the
    /// per-key reducer rule (last-write-wins for general keys, append for
    /// <see cref="WellKnownKey.Messages"/>). Returns the list of keys that changed.
    /// </summary>
    public static IReadOnlyList<string> Merge(
        IDictionary<string, JsonElement> state,
        IReadOnlyDictionary<string, JsonElement> incoming)
    {
        var changed = new List<string>();
        foreach (var (key, value) in incoming)
        {
            if (state.TryGetValue(key, out var existing))
            {
                var reduced = key == WellKnownKey.Messages
                    ? AppendMessages(existing, value)
                    : LastWriteWins(existing, value);
                if (!JsonElementEqual(existing, reduced))
                {
                    state[key] = reduced;
                    changed.Add(key);
                }
            }
            else
            {
                state[key] = value;
                changed.Add(key);
            }
        }
        return changed;
    }

    private static bool JsonElementEqual(JsonElement a, JsonElement b)
        => a.GetRawText() == b.GetRawText();
}
