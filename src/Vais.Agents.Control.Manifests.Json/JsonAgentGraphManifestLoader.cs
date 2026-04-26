// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Parses <see cref="AgentGraphManifest"/> records from JSON text. Accepts a single
/// document or a JSON array of documents. Schema matches the v0.6 envelope — same
/// shape as <see cref="JsonAgentManifestLoader"/> with <c>kind: AgentGraph</c>.
/// Validates per <see cref="AgentGraphManifestValidator"/>; throws
/// <see cref="AgentManifestValidationException"/> with every violation found in one pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shipped as a separate entry point</b> from <see cref="JsonAgentManifestLoader"/>
/// so existing consumers calling <c>LoadFromStringAsync</c> for <c>kind: Agent</c>
/// remain source-compatible. Mixed-kind streams flow through
/// <see cref="LoadAllResourcesFromStringAsync"/>, which returns a polymorphic
/// <see cref="ManifestResource"/> list preserving order.
/// </para>
/// </remarks>
public sealed class JsonAgentGraphManifestLoader
{
    internal const string ExpectedApiVersion = "vais.agents/v1";
    internal const string AgentGraphKind = "AgentGraph";
    internal const string AgentKind = "Agent";

    /// <summary>Parse graph manifests from an in-memory string. Agent-kind documents in the stream are silently skipped.</summary>
    public ValueTask<IReadOnlyList<AgentGraphManifest>> LoadFromStringAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var errors = new List<string>();
        var result = ParseAndValidate(content, errors)
            .OfType<ManifestResource.AgentGraphCase>()
            .Select(r => r.Graph)
            .ToList();
        if (errors.Count > 0)
        {
            throw new AgentManifestValidationException(errors);
        }
        return ValueTask.FromResult<IReadOnlyList<AgentGraphManifest>>(result);
    }

    /// <summary>Parse graph manifests from a single file on disk. Agent-kind documents are silently skipped.</summary>
    public async ValueTask<IReadOnlyList<AgentGraphManifest>> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await LoadFromStringAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parse any mix of <c>kind: Agent</c> + <c>kind: AgentGraph</c> documents, preserving order.</summary>
    public ValueTask<IReadOnlyList<ManifestResource>> LoadAllResourcesFromStringAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var errors = new List<string>();
        var result = ParseAndValidate(content, errors);
        if (errors.Count > 0)
        {
            throw new AgentManifestValidationException(errors);
        }
        return ValueTask.FromResult<IReadOnlyList<ManifestResource>>(result);
    }

    internal static IReadOnlyList<ManifestResource> ParseAndValidate(string content, List<string> errors, string? sourceHint = null)
    {
        var prefix = sourceHint is null ? string.Empty : $"{Path.GetFileName(sourceHint)}: ";
        if (string.IsNullOrWhiteSpace(content))
        {
            errors.Add($"{prefix}empty document");
            return Array.Empty<ManifestResource>();
        }

        using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true });
        var resources = new List<ManifestResource>();
        var graphsForSecondPass = new List<(AgentGraphManifest Graph, string Prefix)>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var itemPrefix = $"{prefix}[{index}] ";
                var parsed = ParseSingle(item, errors, itemPrefix);
                if (parsed is not null)
                {
                    resources.Add(parsed);
                    if (parsed is ManifestResource.AgentGraphCase gc)
                        graphsForSecondPass.Add((gc.Graph, itemPrefix));
                }
                index++;
            }
        }
        else
        {
            var parsed = ParseSingle(doc.RootElement, errors, prefix);
            if (parsed is not null)
            {
                resources.Add(parsed);
                if (parsed is ManifestResource.AgentGraphCase gc)
                    graphsForSecondPass.Add((gc.Graph, prefix));
            }
        }

        CheckDuplicateIds(resources, errors);
        RunAgentOutputSchemaSecondPass(resources, graphsForSecondPass, errors);
        return resources;
    }

    private static void RunAgentOutputSchemaSecondPass(
        List<ManifestResource> resources,
        List<(AgentGraphManifest Graph, string Prefix)> graphs,
        List<string> errors)
    {
        if (graphs.Count == 0) return;

        var inStreamAgents = resources.OfType<ManifestResource.AgentCase>()
            .Select(a => a.Manifest).ToList();
        if (inStreamAgents.Count == 0) return;

        var byIdVersion = new Dictionary<(string, string), AgentManifest>(capacity: inStreamAgents.Count);
        var byId = new Dictionary<string, AgentManifest>(StringComparer.Ordinal);
        foreach (var agent in inStreamAgents)
        {
            byIdVersion.TryAdd((agent.Id, agent.Version), agent);
            if (!byId.TryGetValue(agent.Id, out var existing) ||
                string.Compare(agent.Version, existing.Version, StringComparison.Ordinal) > 0)
            {
                byId[agent.Id] = agent;
            }
        }

        AgentManifest? Resolver(string id, string? version)
        {
            if (version is not null)
                return byIdVersion.TryGetValue((id, version), out var m) ? m : null;
            return byId.TryGetValue(id, out var latest) ? latest : null;
        }

        foreach (var (graph, graphPrefix) in graphs)
            AgentGraphManifestValidator.ValidateAgentOutputSchemaBindings(graph, Resolver, errors, graphPrefix);
    }

    private static ManifestResource? ParseSingle(JsonElement root, List<string> errors, string prefix)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}document must be a JSON object");
            return null;
        }

        var apiVersion = root.TryGetProperty("apiVersion", out var av) ? av.GetString() : null;
        if (!string.Equals(apiVersion, ExpectedApiVersion, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}unexpected apiVersion '{apiVersion ?? "<null>"}' (expected '{ExpectedApiVersion}')");
            return null;
        }

        var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
        if (string.Equals(kind, AgentKind, StringComparison.Ordinal))
        {
            // Delegate Agent-kind parsing to the existing loader's shared core.
            var agents = JsonAgentManifestLoader.ParseAndValidate(root.GetRawText(), errors, sourceHint: null);
            return agents.Count == 1 ? new ManifestResource.AgentCase(agents[0]) : null;
        }
        if (string.Equals(kind, AgentGraphKind, StringComparison.Ordinal))
        {
            var graph = ParseGraph(root, errors, prefix);
            return graph is null ? null : new ManifestResource.AgentGraphCase(graph);
        }

        errors.Add($"{prefix}unexpected kind '{kind ?? "<null>"}' (expected '{AgentKind}' or '{AgentGraphKind}')");
        return null;
    }

    private static AgentGraphManifest? ParseGraph(JsonElement root, List<string> errors, string prefix)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid metadata block");
            return null;
        }

        var id = metadata.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var version = metadata.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
        var description = metadata.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;

        if (string.IsNullOrEmpty(id))
        {
            errors.Add($"{prefix}metadata.id is required");
        }
        else if (!ManifestValidation.IsValidId(id))
        {
            errors.Add($"{prefix}metadata.id '{id}' does not match ^[a-z][a-z0-9-]{{0,62}}$");
        }
        if (string.IsNullOrEmpty(version))
        {
            errors.Add($"{prefix}metadata.version is required");
        }
        else if (!ManifestValidation.IsValidVersion(version))
        {
            errors.Add($"{prefix}metadata.version '{version}' does not match ^\\d+\\.\\d+(\\.\\d+)?$");
        }

        var labels = ParseStringMap(metadata, "labels");
        var annotations = ParseStringMap(metadata, "annotations");

        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid spec block");
            return null;
        }

        var entry = spec.TryGetProperty("entry", out var eEl) ? eEl.GetString() : null;
        if (string.IsNullOrEmpty(entry))
        {
            errors.Add($"{prefix}spec.entry is required");
        }

        var nodes = ParseNodes(spec, errors, prefix);
        var edges = ParseEdges(spec, errors, prefix);

        JsonElement? stateSchema = spec.TryGetProperty("state", out var stateEl) && stateEl.ValueKind == JsonValueKind.Object &&
                                    stateEl.TryGetProperty("schema", out var schemaEl) ? schemaEl.Clone() : null;
        int? maxSteps = spec.TryGetProperty("maxSteps", out var msEl) && msEl.ValueKind == JsonValueKind.Number
            ? msEl.GetInt32() : null;

        var stateReducers = ParseStateReducers(spec, errors, prefix);

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version) || string.IsNullOrEmpty(entry))
        {
            return null;
        }

        var manifest = new AgentGraphManifest(
            Id: id!, Version: version!, Entry: entry!,
            Nodes: nodes, Edges: edges,
            Description: description,
            Labels: labels,
            Annotations: annotations)
        {
            StateSchema = stateSchema,
            MaxSteps = maxSteps,
            StateReducers = stateReducers,
        };

        AgentGraphManifestValidator.Validate(manifest, errors, prefix);
        return manifest;
    }

    private static IReadOnlyList<GraphNode> ParseNodes(JsonElement spec, List<string> errors, string prefix)
    {
        if (!spec.TryGetProperty("nodes", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{prefix}spec.nodes is required and must be an array");
            return Array.Empty<GraphNode>();
        }
        var list = new List<GraphNode>();
        var index = 0;
        foreach (var item in arr.EnumerateArray())
        {
            var itemPrefix = $"{prefix}spec.nodes[{index}] ";
            var nodeId = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var kind = item.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;
            if (string.IsNullOrEmpty(nodeId))
            {
                errors.Add($"{itemPrefix}id is required");
                index++;
                continue;
            }
            if (string.IsNullOrEmpty(kind))
            {
                errors.Add($"{itemPrefix}kind is required");
                index++;
                continue;
            }

            GraphAgentRef? agentRef = null;
            if (item.TryGetProperty("ref", out var refEl) && refEl.ValueKind == JsonValueKind.Object)
            {
                var refId = refEl.TryGetProperty("id", out var rIdEl) ? rIdEl.GetString() : null;
                var refVersion = refEl.TryGetProperty("version", out var rvEl) ? rvEl.GetString() : null;
                var runtimeUrl = refEl.TryGetProperty("runtimeUrl", out var ruEl) ? ruEl.GetString() : null;
                if (string.IsNullOrEmpty(refId))
                {
                    errors.Add($"{itemPrefix}ref.id is required");
                }
                else
                {
                    if (runtimeUrl is not null)
                    {
                        if (!Uri.TryCreate(runtimeUrl, UriKind.Absolute, out var parsedUri)
                            || parsedUri.Scheme is not ("http" or "https"))
                        {
                            errors.Add($"{itemPrefix}ref.runtimeUrl '{runtimeUrl}' must be an absolute http or https URI");
                            runtimeUrl = null;
                        }
                    }

                    var a2aUrl = refEl.TryGetProperty("a2aUrl", out var a2aEl) ? a2aEl.GetString() : null;
                    if (a2aUrl is not null)
                    {
                        if (!Uri.TryCreate(a2aUrl, UriKind.Absolute, out var parsedA2a)
                            || parsedA2a.Scheme is not ("http" or "https"))
                        {
                            errors.Add($"{itemPrefix}ref.a2aUrl '{a2aUrl}' must be an absolute http or https URI");
                            a2aUrl = null;
                        }
                    }

                    if (runtimeUrl is not null && a2aUrl is not null)
                    {
                        errors.Add($"{itemPrefix}ref.runtimeUrl and ref.a2aUrl are mutually exclusive — specify exactly one remote endpoint");
                        a2aUrl = null;
                    }

                    agentRef = new GraphAgentRef(refId, refVersion, runtimeUrl, a2aUrl);
                }
            }

            GraphHandlerRef? handlerRef = null;
            if (item.TryGetProperty("handlerRef", out var hrEl) && hrEl.ValueKind == JsonValueKind.Object)
            {
                var tName = hrEl.TryGetProperty("typeName", out var tnEl) ? tnEl.GetString() : null;
                var aName = hrEl.TryGetProperty("assemblyName", out var anEl) ? anEl.GetString() : null;
                if (string.IsNullOrEmpty(tName))
                {
                    errors.Add($"{itemPrefix}handlerRef.typeName is required");
                }
                else
                {
                    handlerRef = new GraphHandlerRef(tName, aName);
                }
            }

            GraphStateBindings? bindings = null;
            if (item.TryGetProperty("stateBindings", out var sbEl) && sbEl.ValueKind == JsonValueKind.Object)
            {
                bindings = new GraphStateBindings(
                    Input: ParseStringArray(sbEl, "input"),
                    Output: ParseStringArray(sbEl, "output"));
            }

            var interruptReason = item.TryGetProperty("interruptReason", out var irEl) ? irEl.GetString() : null;

            list.Add(new GraphNode(nodeId!, kind!, agentRef, handlerRef, bindings, interruptReason));
            index++;
        }
        return list;
    }

    private static IReadOnlyList<GraphEdge> ParseEdges(JsonElement spec, List<string> errors, string prefix)
    {
        if (!spec.TryGetProperty("edges", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{prefix}spec.edges is required and must be an array");
            return Array.Empty<GraphEdge>();
        }
        var list = new List<GraphEdge>();
        var index = 0;
        foreach (var item in arr.EnumerateArray())
        {
            var itemPrefix = $"{prefix}spec.edges[{index}] ";
            var from = item.TryGetProperty("from", out var fEl) ? fEl.GetString() : null;
            var to = item.TryGetProperty("to", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
            {
                errors.Add($"{itemPrefix}from + to are required");
                index++;
                continue;
            }

            GraphEdgePredicate? when = null;
            if (item.TryGetProperty("when", out var wEl))
            {
                when = ParsePredicate(wEl, errors, itemPrefix + "when ");
            }

            GraphEdgeEffect? onTraverse = null;
            if (item.TryGetProperty("onTraverse", out var otEl))
            {
                onTraverse = ParseEffect(otEl, errors, itemPrefix + "onTraverse ");
            }

            list.Add(new GraphEdge(from!, to!, when, onTraverse));
            index++;
        }
        return list;
    }

    private static IReadOnlyDictionary<string, GraphStateReducer>? ParseStateReducers(JsonElement spec, List<string> errors, string prefix)
    {
        if (!spec.TryGetProperty("stateReducers", out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var map = new Dictionary<string, GraphStateReducer>(StringComparer.Ordinal);
        foreach (var prop in el.EnumerateObject())
        {
            var key = prop.Name;
            var reducerEl = prop.Value;
            var reducer = ParseReducer(reducerEl, errors, $"{prefix}spec.stateReducers.{key} ");
            if (reducer is not null)
            {
                map[key] = reducer;
            }
        }
        return map.Count == 0 ? null : map;
    }

    private static GraphStateReducer? ParseReducer(JsonElement el, List<string> errors, string prefix)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString() switch
            {
                "lastWriteWins" => new GraphStateReducer.LastWriteWins(),
                "append" => new GraphStateReducer.Append(),
                _ => ReportUnknown(el.GetString()!, errors, prefix),
            };
        }
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("handlerRef", out var hrEl) && hrEl.ValueKind == JsonValueKind.Object)
            {
                var typeName = hrEl.TryGetProperty("typeName", out var tnEl) ? tnEl.GetString() : null;
                var asmName = hrEl.TryGetProperty("assemblyName", out var anEl) ? anEl.GetString() : null;
                if (string.IsNullOrEmpty(typeName))
                {
                    errors.Add($"{prefix}handlerRef.typeName is required");
                    return null;
                }
                return new GraphStateReducer.HandlerRef(new GraphHandlerRef(typeName, asmName));
            }
            errors.Add($"{prefix}must be the string 'lastWriteWins' or 'append', or an object with handlerRef");
            return null;
        }
        errors.Add($"{prefix}must be a string ('lastWriteWins' | 'append') or an object with handlerRef");
        return null;

        static GraphStateReducer? ReportUnknown(string name, List<string> errors, string prefix)
        {
            errors.Add($"{prefix}unknown reducer '{name}' — expected 'lastWriteWins' or 'append'");
            return null;
        }
    }

    private static GraphEdgePredicate? ParsePredicate(JsonElement el, List<string> errors, string prefix)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            return string.Equals(el.GetString(), "always", StringComparison.Ordinal)
                ? new GraphEdgePredicate.Always()
                : null;
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}must be an object or the string 'always'");
            return null;
        }

        if (el.TryGetProperty("allOf", out var allEl) && allEl.ValueKind == JsonValueKind.Array)
        {
            var children = new List<GraphEdgePredicate>();
            foreach (var child in allEl.EnumerateArray())
            {
                var parsed = ParsePredicate(child, errors, prefix);
                if (parsed is not null) children.Add(parsed);
            }
            return new GraphEdgePredicate.AllOf(children);
        }
        if (el.TryGetProperty("anyOf", out var anyEl) && anyEl.ValueKind == JsonValueKind.Array)
        {
            var children = new List<GraphEdgePredicate>();
            foreach (var child in anyEl.EnumerateArray())
            {
                var parsed = ParsePredicate(child, errors, prefix);
                if (parsed is not null) children.Add(parsed);
            }
            return new GraphEdgePredicate.AnyOf(children);
        }
        if (el.TryGetProperty("not", out var notEl))
        {
            var inner = ParsePredicate(notEl, errors, prefix);
            return inner is null ? null : new GraphEdgePredicate.Not(inner);
        }
        if (el.TryGetProperty("handlerRef", out var hrEl) && hrEl.ValueKind == JsonValueKind.Object)
        {
            var typeName = hrEl.TryGetProperty("typeName", out var tnEl) ? tnEl.GetString() : null;
            var asmName = hrEl.TryGetProperty("assemblyName", out var anEl) ? anEl.GetString() : null;
            if (string.IsNullOrEmpty(typeName))
            {
                errors.Add($"{prefix}handlerRef.typeName is required");
                return null;
            }
            return new GraphEdgePredicate.HandlerRef(new GraphHandlerRef(typeName, asmName));
        }

        // Default = PropertyMatcher
        var property = el.TryGetProperty("property", out var pEl) ? pEl.GetString() : null;
        var opStr = el.TryGetProperty("operator", out var opEl) ? opEl.GetString() : null;
        if (string.IsNullOrEmpty(property) || string.IsNullOrEmpty(opStr))
        {
            errors.Add($"{prefix}property + operator are required for a PropertyMatcher predicate");
            return null;
        }
        if (!Enum.TryParse<GraphPredicateOperator>(opStr, ignoreCase: true, out var op))
        {
            errors.Add($"{prefix}operator '{opStr}' is not a known GraphPredicateOperator");
            return null;
        }
        JsonElement? value = el.TryGetProperty("value", out var vEl) ? vEl.Clone() : null;
        return new GraphEdgePredicate.PropertyMatcher(property!, op, value);
    }

    private static GraphEdgeEffect? ParseEffect(JsonElement el, List<string> errors, string prefix)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}must be an object");
            return null;
        }

        if (el.TryGetProperty("set", out var setEl) && setEl.ValueKind == JsonValueKind.Object)
        {
            var prop = setEl.TryGetProperty("property", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrEmpty(prop) || !setEl.TryGetProperty("value", out var vEl))
            {
                errors.Add($"{prefix}set.property + set.value are required");
                return null;
            }
            return new GraphEdgeEffect.Set(prop!, vEl.Clone());
        }
        if (el.TryGetProperty("increment", out var incEl) && incEl.ValueKind == JsonValueKind.Object)
        {
            var prop = incEl.TryGetProperty("property", out var pEl) ? pEl.GetString() : null;
            var by = incEl.TryGetProperty("by", out var byEl) && byEl.ValueKind == JsonValueKind.Number ? byEl.GetInt32() : 1;
            if (string.IsNullOrEmpty(prop))
            {
                errors.Add($"{prefix}increment.property is required");
                return null;
            }
            return new GraphEdgeEffect.Increment(prop!, by);
        }
        if (el.TryGetProperty("append", out var appEl) && appEl.ValueKind == JsonValueKind.Object)
        {
            var prop = appEl.TryGetProperty("property", out var pEl) ? pEl.GetString() : null;
            if (string.IsNullOrEmpty(prop) || !appEl.TryGetProperty("value", out var vEl))
            {
                errors.Add($"{prefix}append.property + append.value are required");
                return null;
            }
            return new GraphEdgeEffect.Append(prop!, vEl.Clone());
        }
        if (el.TryGetProperty("handlerRef", out var hrEl) && hrEl.ValueKind == JsonValueKind.Object)
        {
            var typeName = hrEl.TryGetProperty("typeName", out var tnEl) ? tnEl.GetString() : null;
            var asmName = hrEl.TryGetProperty("assemblyName", out var anEl) ? anEl.GetString() : null;
            if (string.IsNullOrEmpty(typeName))
            {
                errors.Add($"{prefix}handlerRef.typeName is required");
                return null;
            }
            return new GraphEdgeEffect.HandlerRef(new GraphHandlerRef(typeName, asmName));
        }

        errors.Add($"{prefix}must contain exactly one of set / increment / append / handlerRef");
        return null;
    }

    private static IReadOnlyList<string>? ParseStringArray(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list.Count == 0 ? null : list;
    }

    private static IReadOnlyDictionary<string, string>? ParseStringMap(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var map = new Dictionary<string, string>();
        foreach (var prop in mapEl.EnumerateObject())
        {
            var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
            map[prop.Name] = value ?? string.Empty;
        }
        return map;
    }

    private static void CheckDuplicateIds(IEnumerable<ManifestResource> resources, List<string> errors)
    {
        var seen = new HashSet<(string Kind, string Id, string Version)>();
        foreach (var r in resources)
        {
            var key = r switch
            {
                ManifestResource.AgentCase a => ("Agent", a.Manifest.Id, a.Manifest.Version),
                ManifestResource.AgentGraphCase g => ("AgentGraph", g.Graph.Id, g.Graph.Version),
                _ => throw new NotSupportedException(),
            };
            if (!seen.Add(key))
            {
                errors.Add($"duplicate manifest: kind='{key.Item1}' id='{key.Item2}' version='{key.Item3}'");
            }
        }
    }
}
