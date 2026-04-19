// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// Evaluates <see cref="GraphEdgePredicate"/>s against graph state. Internal helper
/// shared by <see cref="InProcessGraphOrchestrator"/> and (eventually) the MAF
/// adapter's conditional-edge translator.
/// </summary>
internal static class GraphPredicateEvaluator
{
    /// <summary>
    /// Evaluate a predicate. <paramref name="handlerResolver"/> is consulted for
    /// <see cref="GraphEdgePredicate.HandlerRef"/> nodes; null means handlerRef
    /// predicates throw when reached.
    /// </summary>
    public static async ValueTask<bool> EvaluateAsync(
        GraphEdgePredicate? predicate,
        IReadOnlyDictionary<string, JsonElement> state,
        Func<GraphHandlerRef, IGraphEdgePredicate>? handlerResolver,
        CancellationToken cancellationToken)
    {
        if (predicate is null || predicate is GraphEdgePredicate.Always)
        {
            return true;
        }

        switch (predicate)
        {
            case GraphEdgePredicate.PropertyMatcher m:
                return EvaluatePropertyMatcher(m, state);

            case GraphEdgePredicate.AllOf a:
                foreach (var p in a.Predicates)
                {
                    if (!await EvaluateAsync(p, state, handlerResolver, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
                return true;

            case GraphEdgePredicate.AnyOf a:
                foreach (var p in a.Predicates)
                {
                    if (await EvaluateAsync(p, state, handlerResolver, cancellationToken).ConfigureAwait(false))
                    {
                        return true;
                    }
                }
                return false;

            case GraphEdgePredicate.Not n:
                return !await EvaluateAsync(n.Predicate, state, handlerResolver, cancellationToken).ConfigureAwait(false);

            case GraphEdgePredicate.HandlerRef h:
                if (handlerResolver is null)
                {
                    throw new InvalidOperationException(
                        $"Predicate references handler '{h.Handler.TypeName}' but no resolver was supplied.");
                }
                var handler = handlerResolver(h.Handler);
                return await handler.EvaluateAsync(state, cancellationToken).ConfigureAwait(false);

            default:
                throw new NotSupportedException($"Unknown predicate subtype '{predicate.GetType().Name}'.");
        }
    }

    private static bool EvaluatePropertyMatcher(
        GraphEdgePredicate.PropertyMatcher matcher,
        IReadOnlyDictionary<string, JsonElement> state)
    {
        var exists = TryResolveProperty(matcher.Property, state, out var value);

        return matcher.Operator switch
        {
            GraphPredicateOperator.Exists => exists,
            GraphPredicateOperator.NotExists => !exists,
            _ when !exists => false,
            GraphPredicateOperator.Eq => JsonEquals(value, matcher.Value),
            GraphPredicateOperator.NotEq => !JsonEquals(value, matcher.Value),
            GraphPredicateOperator.Gt => CompareNumbers(value, matcher.Value) > 0,
            GraphPredicateOperator.Gte => CompareNumbers(value, matcher.Value) >= 0,
            GraphPredicateOperator.Lt => CompareNumbers(value, matcher.Value) < 0,
            GraphPredicateOperator.Lte => CompareNumbers(value, matcher.Value) <= 0,
            GraphPredicateOperator.Contains => ContainsValue(value, matcher.Value),
            GraphPredicateOperator.NotContains => !ContainsValue(value, matcher.Value),
            _ => throw new NotSupportedException($"Unknown predicate operator '{matcher.Operator}'."),
        };
    }

    /// <summary>
    /// Resolves a dotted property path. Top-level keys read from state; the well-known
    /// <c>lastMessage.text</c> / <c>lastMessage.role</c> / <c>lastMessage.*</c> paths
    /// read from the most-recently-appended <c>ChatTurn</c> in the <c>messages</c>
    /// state key (standard reducer output).
    /// </summary>
    internal static bool TryResolveProperty(
        string path,
        IReadOnlyDictionary<string, JsonElement> state,
        out JsonElement value)
    {
        value = default;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var dotIndex = path.IndexOf('.');
        if (dotIndex < 0)
        {
            if (state.TryGetValue(path, out var flat))
            {
                value = flat;
                return true;
            }
            return false;
        }

        var head = path[..dotIndex];
        var tail = path[(dotIndex + 1)..];

        // Well-known computed paths read from the latest message in state["messages"].
        if (head == "lastMessage" && state.TryGetValue("messages", out var messages) &&
            messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0)
        {
            var last = messages[messages.GetArrayLength() - 1];
            if (last.ValueKind == JsonValueKind.Object && last.TryGetProperty(tail, out var prop))
            {
                value = prop;
                return true;
            }
            return false;
        }

        // General dotted-path access on state["head"] (nested JSON object access).
        if (state.TryGetValue(head, out var container) && container.ValueKind == JsonValueKind.Object)
        {
            if (container.TryGetProperty(tail, out var prop))
            {
                value = prop;
                return true;
            }
        }
        return false;
    }

    private static bool JsonEquals(JsonElement a, JsonElement? b)
    {
        if (b is null) return a.ValueKind == JsonValueKind.Null;
        return a.ValueKind switch
        {
            JsonValueKind.String => b.Value.ValueKind == JsonValueKind.String && a.GetString() == b.Value.GetString(),
            JsonValueKind.Number => b.Value.ValueKind == JsonValueKind.Number && a.GetDouble() == b.Value.GetDouble(),
            JsonValueKind.True => b.Value.ValueKind == JsonValueKind.True,
            JsonValueKind.False => b.Value.ValueKind == JsonValueKind.False,
            JsonValueKind.Null => b.Value.ValueKind == JsonValueKind.Null,
            _ => a.GetRawText() == b.Value.GetRawText(),
        };
    }

    private static int CompareNumbers(JsonElement a, JsonElement? b)
    {
        if (b is null || a.ValueKind != JsonValueKind.Number || b.Value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException("Numeric comparison requires both sides to be JSON numbers.");
        }
        return a.GetDouble().CompareTo(b.Value.GetDouble());
    }

    private static bool ContainsValue(JsonElement haystack, JsonElement? needle)
    {
        if (needle is null) return false;
        if (haystack.ValueKind == JsonValueKind.String && needle.Value.ValueKind == JsonValueKind.String)
        {
            return haystack.GetString()!.Contains(needle.Value.GetString()!, StringComparison.Ordinal);
        }
        if (haystack.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in haystack.EnumerateArray())
            {
                if (JsonEquals(item, needle))
                {
                    return true;
                }
            }
            return false;
        }
        return false;
    }
}
