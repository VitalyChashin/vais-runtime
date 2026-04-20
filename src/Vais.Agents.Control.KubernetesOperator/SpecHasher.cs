// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Computes a stable SHA-256 fingerprint of an <see cref="AgentSpec"/>
/// used by <see cref="AgentStatus.ManifestRevision"/> for diff-based
/// reconcile. Two specs that JSON-serialise to the same canonical tree
/// hash identically regardless of property-declaration order; missing
/// optionals, empty collections, and null-vs-absent behave predictably.
/// </summary>
internal static class SpecHasher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Compute <c>sha256:&lt;hex&gt;</c> of the canonical-JSON projection
    /// of <paramref name="spec"/>. Canonical-JSON here = web-case
    /// property names, null values elided, object keys sorted alphabetically
    /// at every depth. Stable across STJ property-ordering differences.
    /// </summary>
    public static string Compute(AgentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var json = JsonSerializer.Serialize(spec, SerializerOptions);
        var node = JsonNode.Parse(json);
        var canonical = Canonicalise(node);
        var canonicalJson = canonical?.ToJsonString(SerializerOptions) ?? "null";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return $"sha256:{Convert.ToHexStringLower(bytes)}";
    }

    private static JsonNode? Canonicalise(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject obj => CanonicaliseObject(obj),
            JsonArray arr => CanonicaliseArray(arr),
            JsonValue val => val.DeepClone(),
            _ => node.DeepClone(),
        };
    }

    private static JsonObject CanonicaliseObject(JsonObject source)
    {
        var sorted = new JsonObject();
        foreach (var key in source.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            sorted[key] = Canonicalise(source[key]);
        }
        return sorted;
    }

    private static JsonArray CanonicaliseArray(JsonArray source)
    {
        var copy = new JsonArray();
        foreach (var item in source)
        {
            copy.Add(Canonicalise(item));
        }
        return copy;
    }
}
