// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Shared nested reflection walker backing the manifest round-trip coverage guards
/// (the graph analogue of <see cref="ManifestFieldRoundTripTests"/>, extended to
/// every hand-written-serializer kind). Discovers every dotted field path reachable
/// from a manifest record, recursing into nested <c>Vais.Agents</c> records — directly
/// or as the element/value of <see cref="IReadOnlyList{T}"/> / <see cref="IReadOnlyDictionary{K,V}"/>.
/// </summary>
/// <remarks>
/// Leaves (not recursed): scalars, strings, string maps/lists, <c>JsonElement</c>,
/// <c>TimeSpan</c>/<c>Uri</c>, enums, and abstract closed-hierarchy types
/// (<c>GraphEdgePredicate</c>/<c>GraphEdgeEffect</c>/<c>GraphStateReducer</c>). Their
/// per-value / per-subtype coverage lives in dedicated tests
/// (<c>ManifestEnumRoundTripTests</c>, <c>EnvelopeSerializerGraphEdgeTests</c>, …) —
/// the same split philosophy as the flat M1.3 walker.
/// </remarks>
internal static class ManifestRoundTripWalker
{
    /// <summary>
    /// All optional-field dotted paths reachable from <paramref name="root"/>, minus the
    /// structural required fields in <paramref name="alwaysSerialized"/> (e.g. <c>Id</c>,
    /// <c>Version</c>, required nested scalars always emitted by the serializer).
    /// </summary>
    public static IReadOnlyList<string> Discover(Type root, IReadOnlySet<string> alwaysSerialized)
        => Walk(root, alwaysSerialized, prefix: "");

    private static List<string> Walk(Type type, IReadOnlySet<string> alwaysSerialized, string prefix)
    {
        var results = new List<string>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
            if (alwaysSerialized.Contains(path)) continue;

            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (underlying.IsEnum) continue; // covered by ManifestEnumRoundTripTests

            var recordToRecurse = ConcreteRecordElement(prop.PropertyType);
            if (recordToRecurse is not null)
            {
                results.AddRange(Walk(recordToRecurse, alwaysSerialized, path));
                continue;
            }

            results.Add(path);
        }
        return results;
    }

    // The concrete Vais.Agents record to recurse into for a property typed as such a record
    // (directly, or as the element of IReadOnlyList<>/value of IReadOnlyDictionary<,>).
    // Abstract closed hierarchies are NOT concrete → treated as leaves.
    private static Type? ConcreteRecordElement(Type propType)
    {
        var underlying = Nullable.GetUnderlyingType(propType) ?? propType;
        if (IsConcreteManifestRecord(underlying)) return underlying;

        foreach (var i in new[] { propType }.Concat(propType.GetInterfaces()))
        {
            if (!i.IsGenericType) continue;
            var def = i.GetGenericTypeDefinition();
            if (def == typeof(IReadOnlyList<>) || def == typeof(IList<>) || def == typeof(IEnumerable<>))
            {
                if (IsConcreteManifestRecord(i.GetGenericArguments()[0])) return i.GetGenericArguments()[0];
            }
            else if (def == typeof(IReadOnlyDictionary<,>) || def == typeof(IDictionary<,>))
            {
                if (IsConcreteManifestRecord(i.GetGenericArguments()[1])) return i.GetGenericArguments()[1];
            }
        }
        return null;
    }

    private static bool IsConcreteManifestRecord(Type t)
        => t.IsClass && !t.IsAbstract && t.Namespace == "Vais.Agents" && IsRecord(t);

    private static bool IsRecord(Type t)
        => t.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null;
}
