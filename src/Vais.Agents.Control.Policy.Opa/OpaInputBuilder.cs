// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Policy.Opa;

/// <summary>
/// Builds the <c>input</c> payload Rego policies see. Locked v1 schema:
/// <c>{ schemaVersion, operation, principal, agent }</c> where
/// <c>agent</c> is the full <see cref="AgentManifest"/> serialised via
/// <see cref="JsonSerializerDefaults.Web"/> (camelCase) and
/// <c>principal</c> + <c>agent</c> are nullable per the shipped
/// <see cref="IAgentPolicyEngine"/> contract.
/// </summary>
internal static class OpaInputBuilder
{
    /// <summary>Schema version embedded in every input payload. Bumped when incompatible shape changes land.</summary>
    public const string SchemaVersion = "1";

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Build the input object. Returns a <see cref="JsonObject"/>
    /// because (a) downstream canonicalisation needs structured access
    /// for alphabetical key-sort cache fingerprinting, and (b) STJ
    /// serialises <see cref="JsonObject"/> cleanly on the wire.
    /// </summary>
    public static JsonObject Build(
        PolicyOperation operation,
        AgentManifest? manifest,
        AgentPrincipal? principal)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["operation"] = operation.ToString(),
            ["principal"] = principal is null ? null : SerialisePrincipal(principal),
            ["agent"] = manifest is null ? null : SerialiseManifest(manifest),
        };
        return root;
    }

    /// <summary>
    /// Overload for extension lifecycle operations. Puts <paramref name="extension"/>
    /// under the <c>"extension"</c> key so Rego policies can inspect
    /// extension id, handlers, host, and scope.
    /// </summary>
    public static JsonObject Build(
        PolicyOperation operation,
        ExtensionManifest? extension,
        AgentPrincipal? principal)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["operation"] = operation.ToString(),
            ["principal"] = principal is null ? null : SerialisePrincipal(principal),
            ["extension"] = extension is null ? null : SerialiseExtension(extension),
        };
        return root;
    }

    private static JsonNode? SerialisePrincipal(AgentPrincipal principal)
    {
        var node = new JsonObject
        {
            ["id"] = principal.Id,
            ["tenantId"] = principal.TenantId,
        };
        if (principal.Scopes is { Count: > 0 } scopes)
        {
            var arr = new JsonArray();
            foreach (var s in scopes)
            {
                arr.Add(s);
            }
            node["scopes"] = arr;
        }
        return node;
    }

    private static JsonNode? SerialiseManifest(AgentManifest manifest)
    {
        // Round-trip through JsonSerializer so every public property of
        // AgentManifest (including nested records and JsonElement fields)
        // reaches the wire unchanged. Cheaper than a field-by-field
        // hand-roll, and tracks manifest evolutions automatically.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        return JsonNode.Parse(bytes);
    }

    private static JsonNode? SerialiseExtension(ExtensionManifest extension)
    {
        var node = new JsonObject
        {
            ["id"] = extension.Id,
            ["version"] = extension.Version,
            ["host"] = extension.Spec.Host,
        };
        if (extension.Spec.Handlers is { Count: > 0 } handlers)
        {
            var arr = new JsonArray();
            foreach (var h in handlers)
            {
                arr.Add(new JsonObject { ["id"] = h.Id, ["seam"] = h.Seam });
            }
            node["handlers"] = arr;
        }
        if (extension.Spec.Scope is { } scope)
        {
            var scopeNode = new JsonObject();
            if (scope.Workspaces is { Count: > 0 } ws)
            {
                var arr = new JsonArray();
                foreach (var w in ws) arr.Add(w);
                scopeNode["workspaces"] = arr;
            }
            if (scope.AgentIds is { Count: > 0 } aids)
            {
                var arr = new JsonArray();
                foreach (var a in aids) arr.Add(a);
                scopeNode["agentIds"] = arr;
            }
            node["scope"] = scopeNode;
        }
        if (extension.Labels is { Count: > 0 } labels)
        {
            var labelsNode = new JsonObject();
            foreach (var kvp in labels) labelsNode[kvp.Key] = kvp.Value;
            node["labels"] = labelsNode;
        }
        return node;
    }
}
