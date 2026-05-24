// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Control.Mcp.Server;

/// <summary>
/// Maps the generic <c>kind</c> parameter to the appropriate in-proc registry and
/// serializes results as v0.6 envelope JSON. Used by the <c>vais.list</c>, <c>vais.get</c>,
/// and <c>vais.diff</c> tool handlers.
/// </summary>
internal static class DesignRegistryRouter
{
    private static readonly IReadOnlySet<string> SupportedKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "Agent", "AgentGraph", "McpServer", "LlmGatewayConfig",
        "McpGatewayConfig", "ContainerPlugin", "EvalSuite",
    };

    internal static bool IsSupported(string kind) => SupportedKinds.Contains(kind);

    /// <summary>List all manifests for <paramref name="kind"/>; returns envelope JSON strings.</summary>
    internal static async ValueTask<IReadOnlyList<string>> ListAsync(
        string kind,
        IServiceProvider services,
        string? labelSelector,
        CancellationToken ct)
    {
        var results = new List<string>();
        switch (kind)
        {
            case "Agent":
            {
                var reg = services.GetRequiredService<IAgentRegistry>();
                await foreach (var m in reg.ListAsync(labelSelector, ct).ConfigureAwait(false))
                    results.Add(EnvelopeCodec.Serialize(m, "Agent"));
                break;
            }
            case "AgentGraph":
            {
                var reg = services.GetRequiredService<IAgentGraphRegistry>();
                await foreach (var m in reg.ListAsync(labelSelector, ct).ConfigureAwait(false))
                    results.Add(EnvelopeCodec.Serialize(m, "AgentGraph"));
                break;
            }
            case "McpServer":
            {
                var reg = services.GetRequiredService<IMcpServerRegistry>();
                await foreach (var m in reg.ListAsync(labelSelector, ct).ConfigureAwait(false))
                    results.Add(EnvelopeCodec.Serialize(m, "McpServer"));
                break;
            }
            case "LlmGatewayConfig":
            {
                var reg = services.GetRequiredService<ILlmGatewayConfigRegistry>();
                await foreach (var m in reg.ListAsync(labelSelector, ct).ConfigureAwait(false))
                    results.Add(EnvelopeCodec.Serialize(m, "LlmGatewayConfig"));
                break;
            }
            case "McpGatewayConfig":
            {
                var reg = services.GetRequiredService<IMcpGatewayConfigRegistry>();
                await foreach (var m in reg.ListAsync(labelSelector, ct).ConfigureAwait(false))
                    results.Add(EnvelopeCodec.Serialize(m, "McpGatewayConfig"));
                break;
            }
            case "ContainerPlugin":
            {
                var reg = services.GetRequiredService<IContainerPluginRegistry>();
                await foreach (var m in reg.ListAsync(labelSelector, ct).ConfigureAwait(false))
                    results.Add(EnvelopeCodec.Serialize(m, "ContainerPlugin"));
                break;
            }
            case "EvalSuite":
            {
                var reg = services.GetRequiredService<IEvalSuiteRegistry>();
                await foreach (var m in reg.ListAsync(labelSelector, ct).ConfigureAwait(false))
                    results.Add(EnvelopeCodec.Serialize(m, "EvalSuite"));
                break;
            }
        }
        return results;
    }

    /// <summary>Get a single manifest; returns envelope JSON string or null on miss.</summary>
    internal static async ValueTask<string?> GetAsync(
        string kind,
        string name,
        string? version,
        IServiceProvider services,
        CancellationToken ct)
    {
        return kind switch
        {
            "Agent" => Wrap(await services.GetRequiredService<IAgentRegistry>()
                .GetAsync(name, version, ct).ConfigureAwait(false), "Agent"),
            "AgentGraph" => Wrap(await services.GetRequiredService<IAgentGraphRegistry>()
                .GetAsync(name, version, ct).ConfigureAwait(false), "AgentGraph"),
            "McpServer" => Wrap(await services.GetRequiredService<IMcpServerRegistry>()
                .GetAsync(name, version, ct).ConfigureAwait(false), "McpServer"),
            "LlmGatewayConfig" => Wrap(await services.GetRequiredService<ILlmGatewayConfigRegistry>()
                .GetAsync(name, version, ct).ConfigureAwait(false), "LlmGatewayConfig"),
            "McpGatewayConfig" => Wrap(await services.GetRequiredService<IMcpGatewayConfigRegistry>()
                .GetAsync(name, version, ct).ConfigureAwait(false), "McpGatewayConfig"),
            "ContainerPlugin" => Wrap(await services.GetRequiredService<IContainerPluginRegistry>()
                .GetAsync(name, version, ct).ConfigureAwait(false), "ContainerPlugin"),
            "EvalSuite" => Wrap(await services.GetRequiredService<IEvalSuiteRegistry>()
                .GetAsync(name, version, ct).ConfigureAwait(false), "EvalSuite"),
            _ => null,
        };

        static string? Wrap<T>(T? manifest, string kind) where T : class
            => manifest is null ? null : EnvelopeCodec.Serialize(manifest, kind);
    }

    /// <summary>
    /// Compute a spec-level diff between <paramref name="candidateJson"/> (the caller's
    /// envelope) and the currently registered version. Returns a structured diff object
    /// with <c>added</c>, <c>removed</c>, and <c>changed</c> field lists. Returns null
    /// when the registered version cannot be found (treat as all-added).
    /// </summary>
    internal static async ValueTask<JsonObject> DiffAsync(
        string candidateJson,
        IServiceProvider services,
        CancellationToken ct)
    {
        using var candidateDoc = JsonDocument.Parse(candidateJson);
        var root = candidateDoc.RootElement;

        var kind = root.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
        var name = root.TryGetProperty("metadata", out var metaEl)
            && metaEl.TryGetProperty("id", out var idEl)
                ? idEl.GetString()
                : null;
        var version = metaEl.ValueKind == JsonValueKind.Object
            && metaEl.TryGetProperty("version", out var verEl)
                ? verEl.GetString()
                : null;

        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(name))
        {
            return new JsonObject
            {
                ["error"] = "Manifest must have a 'kind' and 'metadata.id'.",
            };
        }

        var registeredJson = await GetAsync(kind, name, version, services, ct).ConfigureAwait(false);
        if (registeredJson is null)
        {
            return new JsonObject
            {
                ["registered"] = false,
                ["message"] = $"{kind}/{name} is not registered — apply this manifest to create it.",
            };
        }

        using var registeredDoc = JsonDocument.Parse(registeredJson);
        var candidateSpec = root.TryGetProperty("spec", out var cSpecEl) ? cSpecEl : default;
        var registeredSpec = registeredDoc.RootElement.TryGetProperty("spec", out var rSpecEl) ? rSpecEl : default;

        var added = new JsonArray();
        var removed = new JsonArray();
        var changed = new JsonArray();

        // Fields in candidate not in registered, or different.
        if (candidateSpec.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in candidateSpec.EnumerateObject())
            {
                if (registeredSpec.ValueKind != JsonValueKind.Object
                    || !registeredSpec.TryGetProperty(prop.Name, out var existing))
                {
                    added.Add(prop.Name);
                }
                else if (prop.Value.GetRawText() != existing.GetRawText())
                {
                    changed.Add(prop.Name);
                }
            }
        }

        // Fields in registered not in candidate.
        if (registeredSpec.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in registeredSpec.EnumerateObject())
            {
                if (candidateSpec.ValueKind != JsonValueKind.Object
                    || !candidateSpec.TryGetProperty(prop.Name, out _))
                {
                    removed.Add(prop.Name);
                }
            }
        }

        return new JsonObject
        {
            ["kind"] = kind,
            ["name"] = name,
            ["added"] = added,
            ["removed"] = removed,
            ["changed"] = changed,
            ["unchanged"] = added.Count == 0 && removed.Count == 0 && changed.Count == 0,
        };
    }
}
