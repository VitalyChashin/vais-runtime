// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// Default state-reduction strategies used by <see cref="InProcessGraphOrchestrator"/>
/// when merging a node's output bindings back into graph state. Ships two built-in
/// strategies: last-write-wins (the default for all keys) and
/// <see cref="WellKnownKey.Messages"/>-append (array concatenation). Custom per-key
/// overrides are declared via <see cref="AgentGraphManifest.StateReducers"/> and
/// resolved at merge time by <see cref="MergeAsync"/>.
/// </summary>
public static class GraphStateReducers
{
    /// <summary>Well-known state keys the runtime treats with non-default reducers.</summary>
    public static class WellKnownKey
    {
        /// <summary>
        /// Graph-wide chat history. Arrays of <see cref="ChatTurn"/>-shaped JSON objects.
        /// When a node writes to this key, the runtime appends to the existing array
        /// rather than overwriting (<see cref="AppendMessages"/>). Declare an explicit
        /// <see cref="GraphStateReducer.LastWriteWins"/> entry in
        /// <see cref="AgentGraphManifest.StateReducers"/> to opt out.
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
    /// <remarks>This overload is the shipped v0.9 sync surface. Prefer <see cref="MergeAsync"/> for new code that may supply manifest-declared reducers.</remarks>
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

    /// <summary>
    /// Async merge overload that honours per-key reducer declarations from
    /// <see cref="AgentGraphManifest.StateReducers"/>. Precedence: manifest-declared
    /// reducer wins over built-in defaults (<see cref="WellKnownKey.Messages"/> append
    /// or last-write-wins). Falls back to the sync built-in rules for keys not listed
    /// in <paramref name="reducers"/>. Returns the list of keys that changed.
    /// </summary>
    /// <param name="state">Mutable graph state bag. Modified in place.</param>
    /// <param name="incoming">Key/value pairs to merge in.</param>
    /// <param name="reducers">Per-key overrides from the manifest. Null means use built-in defaults for every key.</param>
    /// <param name="reducerResolver">Resolver for <see cref="GraphStateReducer.HandlerRef"/> entries. Null means handler-ref reducers throw <see cref="InvalidOperationException"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async ValueTask<IReadOnlyList<string>> MergeAsync(
        IDictionary<string, JsonElement> state,
        IReadOnlyDictionary<string, JsonElement> incoming,
        IReadOnlyDictionary<string, GraphStateReducer>? reducers,
        Func<GraphHandlerRef, IGraphStateReducer>? reducerResolver,
        CancellationToken cancellationToken = default)
    {
        var changed = new List<string>();
        foreach (var (key, value) in incoming)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingPresent = state.TryGetValue(key, out var existing);

            JsonElement reduced;
            if (reducers is not null && reducers.TryGetValue(key, out var spec))
            {
                // Manifest-declared reducer wins.
                reduced = spec switch
                {
                    GraphStateReducer.LastWriteWins => value,
                    GraphStateReducer.FirstWriteWins => existingPresent ? existing : value,
                    GraphStateReducer.Append => AppendMessages(existingPresent ? existing : default, value),
                    GraphStateReducer.HandlerRef hr when reducerResolver is not null =>
                        await reducerResolver(hr.Handler).ReduceAsync(
                            existingPresent ? existing : default, value, cancellationToken).ConfigureAwait(false),
                    GraphStateReducer.HandlerRef hr =>
                        throw new InvalidOperationException(
                            $"Graph state reducer for key '{key}' references handler '{hr.Handler.TypeName}' but no reducer resolver was supplied."),
                    _ => value,
                };
            }
            else
            {
                // Built-in defaults: append for messages, last-write-wins for everything else.
                reduced = key == WellKnownKey.Messages && existingPresent
                    ? AppendMessages(existing, value)
                    : value;
            }

            if (!existingPresent || !JsonElementEqual(existing, reduced))
            {
                state[key] = reduced;
                changed.Add(key);
            }
        }
        return changed;
    }

    private static bool JsonElementEqual(JsonElement a, JsonElement b)
        => a.GetRawText() == b.GetRawText();
}
