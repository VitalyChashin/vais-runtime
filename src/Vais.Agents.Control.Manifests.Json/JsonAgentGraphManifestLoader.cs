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
    internal const string LlmGatewayConfigKind = "LlmGatewayConfig";
    internal const string McpGatewayConfigKind = "McpGatewayConfig";
    internal const string McpServerKind = "McpServer";
    internal const string ContainerPluginKind = "ContainerPlugin";
    internal const string EvalSuiteKind = "EvalSuite";
    internal const string PluginKind = "Plugin";
    internal const string ExtensionKind = "Extension";

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
        if (string.Equals(kind, LlmGatewayConfigKind, StringComparison.Ordinal))
        {
            var cfg = ParseLlmGatewayConfig(root, errors, prefix);
            return cfg is null ? null : new ManifestResource.LlmGatewayConfigCase(cfg);
        }
        if (string.Equals(kind, McpGatewayConfigKind, StringComparison.Ordinal))
        {
            var cfg = ParseMcpGatewayConfig(root, errors, prefix);
            return cfg is null ? null : new ManifestResource.McpGatewayConfigCase(cfg);
        }
        if (string.Equals(kind, McpServerKind, StringComparison.Ordinal))
        {
            var srv = ParseMcpServer(root, errors, prefix);
            return srv is null ? null : new ManifestResource.McpServerCase(srv);
        }
        if (string.Equals(kind, ContainerPluginKind, StringComparison.Ordinal))
        {
            var plugin = ParseContainerPlugin(root, errors, prefix);
            return plugin is null ? null : new ManifestResource.ContainerPluginCase(plugin);
        }
        if (string.Equals(kind, EvalSuiteKind, StringComparison.Ordinal))
        {
            var suite = EvalSuiteManifestParser.Parse(root, errors, prefix);
            return suite is null ? null : new ManifestResource.EvalSuiteCase(suite);
        }
        if (string.Equals(kind, PluginKind, StringComparison.Ordinal))
        {
            var plugin = ParsePlugin(root, errors, prefix);
            return plugin is null ? null : new ManifestResource.PluginCase(plugin);
        }
        if (string.Equals(kind, ExtensionKind, StringComparison.Ordinal))
        {
            var ext = ParseExtension(root, errors, prefix);
            return ext is null ? null : new ManifestResource.ExtensionCase(ext);
        }

        errors.Add($"{prefix}unexpected kind '{kind ?? "<null>"}' " +
            $"(expected '{AgentKind}', '{AgentGraphKind}', '{LlmGatewayConfigKind}', '{McpGatewayConfigKind}', '{McpServerKind}', '{ContainerPluginKind}', '{EvalSuiteKind}', '{PluginKind}', or '{ExtensionKind}')");
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

            GraphNodeRetryPolicy? retryPolicy = null;
            if (item.TryGetProperty("retryPolicy", out var rpEl) && rpEl.ValueKind == JsonValueKind.Object)
            {
                var maxAttempts = rpEl.TryGetProperty("maxAttempts", out var maEl) && maEl.TryGetInt32(out var ma) ? ma : 1;
                var initialBackoff = rpEl.TryGetProperty("initialBackoffSeconds", out var ibEl) && ibEl.TryGetDouble(out var ib) ? ib : 0.5;
                var multiplier = rpEl.TryGetProperty("backoffMultiplier", out var bmEl) && bmEl.TryGetDouble(out var bm) ? bm : 2.0;
                var maxBackoff = rpEl.TryGetProperty("maxBackoffSeconds", out var mbEl) && mbEl.TryGetDouble(out var mb) ? mb : 30.0;
                retryPolicy = new GraphNodeRetryPolicy(maxAttempts, initialBackoff, multiplier, maxBackoff);
            }

            list.Add(new GraphNode(nodeId!, kind!, agentRef, handlerRef, bindings, interruptReason, retryPolicy));
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

            var concurrent = item.TryGetProperty("concurrent", out var cEl) &&
                             cEl.ValueKind == JsonValueKind.True;
            list.Add(new GraphEdge(from!, to!, when, onTraverse, concurrent));
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
                "firstWriteWins" => new GraphStateReducer.FirstWriteWins(),
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
            errors.Add($"{prefix}must be the string 'lastWriteWins', 'firstWriteWins', 'append', or an object with handlerRef");
            return null;
        }
        errors.Add($"{prefix}must be a string ('lastWriteWins' | 'firstWriteWins' | 'append') or an object with handlerRef");
        return null;

        static GraphStateReducer? ReportUnknown(string name, List<string> errors, string prefix)
        {
            errors.Add($"{prefix}unknown reducer '{name}' — expected 'lastWriteWins', 'firstWriteWins', or 'append'");
            return null;
        }
    }

    private static GraphEdgePredicate? ParsePredicate(JsonElement el, List<string> errors, string prefix)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString()!;
            if (string.Equals(s, "always", StringComparison.Ordinal))
                return new GraphEdgePredicate.Always();
            if (s.StartsWith('='))
                return new GraphEdgePredicate.Expression(s);
            errors.Add($"{prefix}string predicate must be 'always' or start with '=' (PowerFx expression)");
            return null;
        }
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}must be an object, the string 'always', or a '=...' PowerFx expression string");
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
                ManifestResource.LlmGatewayConfigCase l => ("LlmGatewayConfig", l.Config.Id, l.Config.Version),
                ManifestResource.McpGatewayConfigCase m => ("McpGatewayConfig", m.Config.Id, m.Config.Version),
                ManifestResource.McpServerCase s => ("McpServer", s.Server.Id, s.Server.Version),
                ManifestResource.ContainerPluginCase p => ("ContainerPlugin", p.Manifest.Id, p.Manifest.Version),
                ManifestResource.EvalSuiteCase e => ("EvalSuite", e.Suite.Id, e.Suite.Version),
                ManifestResource.PluginCase pl => ("Plugin", pl.Plugin.Id, pl.Plugin.Version),
                ManifestResource.ExtensionCase ex => ("Extension", ex.Extension.Id, ex.Extension.Version),
                _ => throw new NotSupportedException($"Unknown ManifestResource type: {r.GetType().Name}"),
            };
            if (!seen.Add(key))
            {
                errors.Add($"duplicate manifest: kind='{key.Item1}' id='{key.Item2}' version='{key.Item3}'");
            }
        }
    }

    private static (string? Id, string? Version, string? Description, IReadOnlyDictionary<string, string>? Labels, IReadOnlyDictionary<string, string>? Annotations)
        ParseGatewayMetadata(JsonElement root, List<string> errors, string prefix)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid metadata block");
            return (null, null, null, null, null);
        }

        var id = metadata.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var version = metadata.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
        var description = metadata.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;

        if (string.IsNullOrEmpty(id))
            errors.Add($"{prefix}metadata.id is required");
        else if (!ManifestValidation.IsValidId(id))
            errors.Add($"{prefix}metadata.id '{id}' does not match ^[a-z][a-z0-9-]{{0,62}}$");

        if (string.IsNullOrEmpty(version))
            errors.Add($"{prefix}metadata.version is required");
        else if (!ManifestValidation.IsValidVersion(version))
            errors.Add($"{prefix}metadata.version '{version}' does not match ^\\d+\\.\\d+(\\.\\d+)?$");

        var labels = ParseStringMap(metadata, "labels");
        var annotations = ParseStringMap(metadata, "annotations");

        return (id, version, description, labels, annotations);
    }

    private static IReadOnlyList<GatewayMiddlewareSpec> ParseGatewayMiddleware(JsonElement spec, List<string> errors, string prefix)
    {
        if (!spec.TryGetProperty("middleware", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<GatewayMiddlewareSpec>();

        var list = new List<GatewayMiddlewareSpec>();
        var index = 0;
        foreach (var item in arr.EnumerateArray())
        {
            var itemPrefix = $"{prefix}spec.middleware[{index}] ";
            var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                errors.Add($"{itemPrefix}name is required");
                index++;
                continue;
            }
            JsonElement? parms = item.TryGetProperty("params", out var pEl) && pEl.ValueKind != JsonValueKind.Null
                ? pEl.Clone() : null;
            list.Add(new GatewayMiddlewareSpec(name!, parms));
            index++;
        }
        return list;
    }

    private static LlmGatewayConfigManifest? ParseLlmGatewayConfig(JsonElement root, List<string> errors, string prefix)
    {
        var (id, version, description, labels, annotations) = ParseGatewayMetadata(root, errors, prefix);

        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid spec block");
            return null;
        }

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) return null;

        var middleware = ParseGatewayMiddleware(spec, errors, prefix);

        LlmRateLimitSpec? rateLimit = null;
        if (spec.TryGetProperty("rateLimit", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            int? rpm = rl.TryGetProperty("requestsPerMinute", out var rpmEl) && rpmEl.ValueKind == JsonValueKind.Number
                ? rpmEl.GetInt32() : null;
            int? tpm = rl.TryGetProperty("tokensPerMinute", out var tpmEl) && tpmEl.ValueKind == JsonValueKind.Number
                ? tpmEl.GetInt32() : null;
            rateLimit = new LlmRateLimitSpec { RequestsPerMinute = rpm, TokensPerMinute = tpm };
        }

        return new LlmGatewayConfigManifest(id!, version!, middleware, description, labels)
        {
            RateLimit = rateLimit,
            Annotations = annotations,
        };
    }

    private static McpGatewayConfigManifest? ParseMcpGatewayConfig(JsonElement root, List<string> errors, string prefix)
    {
        var (id, version, description, labels, annotations) = ParseGatewayMetadata(root, errors, prefix);

        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid spec block");
            return null;
        }

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) return null;

        var middleware = ParseGatewayMiddleware(spec, errors, prefix);

        IReadOnlyDictionary<string, McpWorkspacePolicySpec>? workspacePolicies = null;
        if (spec.TryGetProperty("workspacePolicies", out var wpEl) && wpEl.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, McpWorkspacePolicySpec>();
            foreach (var prop in wpEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var allowed = ParseStringArray(prop.Value, "allowedTools");
                var denied = ParseStringArray(prop.Value, "deniedTools");
                int minPriv = prop.Value.TryGetProperty("minPrivilegeLevel", out var mpEl) && mpEl.ValueKind == JsonValueKind.Number
                    ? mpEl.GetInt32() : 0;
                map[prop.Name] = new McpWorkspacePolicySpec(allowed, denied, minPriv);
            }
            if (map.Count > 0) workspacePolicies = map;
        }

        return new McpGatewayConfigManifest(id!, version!, middleware, description, labels)
        {
            WorkspacePolicies = workspacePolicies,
            Annotations = annotations,
        };
    }

    private static McpServerManifest? ParseMcpServer(JsonElement root, List<string> errors, string prefix)
    {
        var (id, version, description, labels, annotations) = ParseGatewayMetadata(root, errors, prefix);

        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid spec block");
            return null;
        }

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) return null;

        bool isVirtual = spec.TryGetProperty("virtual", out var vEl) && vEl.ValueKind == JsonValueKind.True;

        string? transport = spec.TryGetProperty("transport", out var tEl) ? tEl.GetString() : null;
        string? url = spec.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
        string? command = spec.TryGetProperty("command", out var cmdEl) ? cmdEl.GetString() : null;
        var args = ParseStringArray(spec, "args");
        var env = ParseStringMap(spec, "env");
        string? authRef = spec.TryGetProperty("authRef", out var arEl) ? arEl.GetString() : null;
        var tools = ParseStringArray(spec, "tools");
        string? mcpGatewayRef = spec.TryGetProperty("mcpGatewayRef", out var mgEl) ? mgEl.GetString() : null;
        string? ontologyRef = spec.TryGetProperty("ontologyRef", out var orEl) ? orEl.GetString() : null;
        string? failureOntologyRef = spec.TryGetProperty("failureOntologyRef", out var faRefEl) ? faRefEl.GetString() : null;

        // Parse virtual sources
        IReadOnlyList<McpServerSourceRef>? sources = null;
        if (spec.TryGetProperty("sources", out var srcArr) && srcArr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<McpServerSourceRef>();
            var idx = 0;
            foreach (var item in srcArr.EnumerateArray())
            {
                var itemPrefix = $"{prefix}spec.sources[{idx}] ";
                var refVal = item.TryGetProperty("ref", out var rEl) ? rEl.GetString() : null;
                if (string.IsNullOrEmpty(refVal))
                    errors.Add($"{itemPrefix}ref is required");
                else
                    list.Add(new McpServerSourceRef(refVal!));
                idx++;
            }
            if (list.Count > 0) sources = list;
        }

        // Parse tool projection
        IReadOnlyList<McpServerToolProjection>? toolProjection = null;
        if (spec.TryGetProperty("toolProjection", out var tpArr) && tpArr.ValueKind == JsonValueKind.Array)
        {
            var list = new List<McpServerToolProjection>();
            var idx = 0;
            foreach (var item in tpArr.EnumerateArray())
            {
                var itemPrefix = $"{prefix}spec.toolProjection[{idx}] ";
                var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                var from = item.TryGetProperty("from", out var fEl) ? fEl.GetString() : null;
                if (string.IsNullOrEmpty(name)) errors.Add($"{itemPrefix}name is required");
                if (string.IsNullOrEmpty(from)) errors.Add($"{itemPrefix}from is required");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(from))
                {
                    var sourceToolName = item.TryGetProperty("sourceToolName", out var stnEl) ? stnEl.GetString() : null;
                    list.Add(new McpServerToolProjection(name!, from!, sourceToolName));
                }
                idx++;
            }
            if (list.Count > 0) toolProjection = list;
        }

        // Parse containerStdio spec
        ContainerMcpSpec? container = null;
        if (spec.TryGetProperty("container", out var containerEl) && containerEl.ValueKind == JsonValueKind.Object)
        {
            container = ParseContainerMcpSpec(containerEl, errors, $"{prefix}spec.container.");
        }

        // Structural validation: virtual vs physical field consistency
        if (isVirtual)
        {
            if (sources is null || sources.Count == 0)
                errors.Add($"{prefix}virtual server must have at least one entry in spec.sources");
            if (transport != null)
                errors.Add($"{prefix}spec.transport must be absent for virtual servers");
            if (container is not null)
                errors.Add($"{prefix}spec.container must be absent for virtual servers");
        }
        else
        {
            if (string.IsNullOrEmpty(transport))
                errors.Add($"{prefix}spec.transport is required for physical servers");
        }

        // Transport-specific validation
        if (string.Equals(transport, "containerStdio", StringComparison.Ordinal))
        {
            if (container is null)
                errors.Add($"{prefix}spec.container is required when transport is 'containerStdio'");
        }
        else if (transport is not null && container is not null)
        {
            errors.Add($"{prefix}spec.container is only valid when transport is 'containerStdio' (got '{transport}')");
        }

        return new McpServerManifest(id!, version!, description, labels)
        {
            Transport = transport,
            Url = url,
            Command = command,
            Args = args,
            Env = env,
            AuthRef = authRef,
            Tools = tools,
            Container = container,
            Virtual = isVirtual,
            Sources = sources,
            ToolProjection = toolProjection,
            McpGatewayRef = mcpGatewayRef,
            OntologyRef = ontologyRef,
            FailureOntologyRef = failureOntologyRef,
            Annotations = annotations,
        };
    }

    private static ContainerMcpSpec? ParseContainerMcpSpec(JsonElement el, List<string> errors, string prefix)
    {
        string? image = el.TryGetProperty("image", out var imgEl) ? imgEl.GetString() : null;

        ContainerMcpBuildSpec? build = null;
        if (el.TryGetProperty("build", out var buildEl) && buildEl.ValueKind == JsonValueKind.Object)
        {
            var context = buildEl.TryGetProperty("context", out var ctxEl) ? ctxEl.GetString() : null;
            if (string.IsNullOrEmpty(context))
            {
                errors.Add($"{prefix}build.context is required when build block is present");
            }
            else
            {
                var dockerfile = buildEl.TryGetProperty("dockerfile", out var dfEl) ? dfEl.GetString() ?? "Dockerfile" : "Dockerfile";
                var buildArgs = ParseStringMap(buildEl, "args");
                var push = buildEl.TryGetProperty("push", out var pushEl) && pushEl.ValueKind == JsonValueKind.True;
                build = new ContainerMcpBuildSpec { Context = context!, Dockerfile = dockerfile, Args = buildArgs, Push = push };
            }
        }

        if (string.IsNullOrEmpty(image) && build is null)
            errors.Add($"{prefix}exactly one of image or build is required");
        else if (!string.IsNullOrEmpty(image) && build is not null)
            errors.Add($"{prefix}image and build are mutually exclusive");

        var port = el.TryGetProperty("port", out var pEl) && pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32() : 7000;
        if (port < 1024 || port > 65535)
            errors.Add($"{prefix}port must be in [1024, 65535] (got {port})");

        var path = el.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "/mcp" : "/mcp";
        var healthPath = el.TryGetProperty("healthPath", out var hpEl) ? hpEl.GetString() ?? "/health" : "/health";

        var command = ParseStringArray(el, "command");
        var args = ParseStringArray(el, "args");
        var env = ParseStringMap(el, "env");
        var secrets = ParseStringMap(el, "secrets");

        var startupTimeout = el.TryGetProperty("startupTimeoutSeconds", out var stEl) && stEl.ValueKind == JsonValueKind.Number
            ? stEl.GetInt32() : 30;
        if (startupTimeout < 1 || startupTimeout > 600)
            errors.Add($"{prefix}startupTimeoutSeconds must be in [1, 600] (got {startupTimeout})");

        var imagePullPolicy = el.TryGetProperty("imagePullPolicy", out var ippEl) ? ippEl.GetString() ?? "IfNotPresent" : "IfNotPresent";

        ContainerMcpResources? resources = null;
        if (el.TryGetProperty("resources", out var resEl) && resEl.ValueKind == JsonValueKind.Object)
        {
            resources = new ContainerMcpResources
            {
                Memory = resEl.TryGetProperty("memory", out var mEl) ? mEl.GetString() : null,
                Cpu = resEl.TryGetProperty("cpu", out var cEl) ? cEl.GetString() : null,
                PidsLimit = resEl.TryGetProperty("pidsLimit", out var pidsEl) && pidsEl.ValueKind == JsonValueKind.Number ? pidsEl.GetInt64() : null,
            };
        }

        ContainerMcpKubernetesConfig? k8s = null;
        if (el.TryGetProperty("kubernetes", out var k8sEl) && k8sEl.ValueKind == JsonValueKind.Object)
        {
            var serviceUrl = k8sEl.TryGetProperty("serviceUrl", out var suEl) ? suEl.GetString() : null;
            if (string.IsNullOrEmpty(serviceUrl))
                errors.Add($"{prefix}kubernetes.serviceUrl is required when kubernetes block is present");
            var deploymentName = k8sEl.TryGetProperty("deploymentName", out var dnEl) ? dnEl.GetString() ?? "" : "";
            var ns = k8sEl.TryGetProperty("namespace", out var nsEl) ? nsEl.GetString() ?? "default" : "default";
            if (!string.IsNullOrEmpty(serviceUrl))
                k8s = new ContainerMcpKubernetesConfig { ServiceUrl = serviceUrl!, DeploymentName = deploymentName, Namespace = ns };
        }

        return new ContainerMcpSpec
        {
            Image = image,
            Build = build,
            Port = port,
            Path = path,
            HealthPath = healthPath,
            Command = command,
            Args = args,
            Env = env,
            Secrets = secrets,
            StartupTimeoutSeconds = startupTimeout,
            ImagePullPolicy = imagePullPolicy,
            Resources = resources,
            Kubernetes = k8s,
        };
    }

    private static ContainerPluginManifest? ParseContainerPlugin(JsonElement root, List<string> errors, string prefix)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid metadata block");
            return null;
        }

        // Accept both metadata.id (standard) and metadata.name (legacy filesystem format).
        var id = metadata.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
            id = metadata.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

        var version = metadata.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
        // Legacy plugin.yaml files omit version; default to "1.0" for backward compat.
        if (string.IsNullOrEmpty(version)) version = "1.0";

        var description = metadata.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;

        if (string.IsNullOrEmpty(id))
            errors.Add($"{prefix}metadata.id (or metadata.name) is required");
        else if (!ManifestValidation.IsValidId(id))
            errors.Add($"{prefix}metadata.id '{id}' does not match ^[a-z][a-z0-9-]{{0,62}}$");

        var labels = ParseStringMap(metadata, "labels");

        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid spec block");
            return null;
        }

        if (string.IsNullOrEmpty(id)) return null;

        var image = spec.TryGetProperty("image", out var imgEl) ? imgEl.GetString() : null;
        if (string.IsNullOrEmpty(image))
            errors.Add($"{prefix}spec.image is required");

        var port = spec.TryGetProperty("port", out var portEl) && portEl.ValueKind == JsonValueKind.Number
            ? portEl.GetInt32() : 8080;
        var topology = spec.TryGetProperty("topology", out var topEl) ? topEl.GetString() ?? "standalone" : "standalone";
        var startupTimeout = spec.TryGetProperty("startupTimeoutSeconds", out var stEl) && stEl.ValueKind == JsonValueKind.Number
            ? stEl.GetInt32() : 30;
        var invokeTimeout = spec.TryGetProperty("invokeTimeoutSeconds", out var itEl) && itEl.ValueKind == JsonValueKind.Number
            ? itEl.GetInt32() : 60;
        int? sessionTtl = spec.TryGetProperty("sessionTtlSeconds", out var sttEl) && sttEl.ValueKind == JsonValueKind.Number
            ? sttEl.GetInt32() : null;
        int? invokeIdleTimeout = spec.TryGetProperty("invokeIdleTimeoutSeconds", out var iitEl) && iitEl.ValueKind == JsonValueKind.Number
            ? iitEl.GetInt32() : null;
        var imagePullPolicy = spec.TryGetProperty("imagePullPolicy", out var ippEl) ? ippEl.GetString() ?? "IfNotPresent" : "IfNotPresent";

        var secrets = ParseStringMap(spec, "secrets");

        ContainerPluginRetryPolicy? retryPolicy = null;
        if (spec.TryGetProperty("retryPolicy", out var rpEl) && rpEl.ValueKind == JsonValueKind.Object)
        {
            var maxAttempts = rpEl.TryGetProperty("maxAttempts", out var maEl) && maEl.ValueKind == JsonValueKind.Number ? maEl.GetInt32() : 3;
            var backoff = rpEl.TryGetProperty("backoffSeconds", out var bEl) && bEl.ValueKind == JsonValueKind.Number ? bEl.GetInt32() : 2;
            var retryOn = ParseStringArray(rpEl, "retryOn") ?? Array.Empty<string>();
            retryPolicy = new ContainerPluginRetryPolicy(maxAttempts, backoff, retryOn);
        }

        ContainerPluginKubernetesConfig? k8sConfig = null;
        if (spec.TryGetProperty("kubernetes", out var k8sEl) && k8sEl.ValueKind == JsonValueKind.Object)
        {
            var serviceUrl = k8sEl.TryGetProperty("serviceUrl", out var suEl) ? suEl.GetString() : null;
            if (string.IsNullOrEmpty(serviceUrl))
                errors.Add($"{prefix}spec.kubernetes.serviceUrl is required when kubernetes block is present");
            var deploymentName = k8sEl.TryGetProperty("deploymentName", out var dnEl) ? dnEl.GetString() ?? "" : "";
            var ns = k8sEl.TryGetProperty("namespace", out var nsEl) ? nsEl.GetString() ?? "default" : "default";
            if (!string.IsNullOrEmpty(serviceUrl))
                k8sConfig = new ContainerPluginKubernetesConfig { ServiceUrl = serviceUrl!, DeploymentName = deploymentName, Namespace = ns };
        }

        ContainerPluginBuildSpec? buildSpec = null;
        if (spec.TryGetProperty("build", out var buildEl) && buildEl.ValueKind == JsonValueKind.Object)
        {
            var context = buildEl.TryGetProperty("context", out var ctxEl) ? ctxEl.GetString() : null;
            if (string.IsNullOrEmpty(context))
                errors.Add($"{prefix}spec.build.context is required when build block is present");
            else
            {
                var dockerfile = buildEl.TryGetProperty("dockerfile", out var dfEl) ? dfEl.GetString() ?? "Dockerfile" : "Dockerfile";
                var buildArgs = ParseStringMap(buildEl, "args");
                var push = buildEl.TryGetProperty("push", out var pushEl) && pushEl.ValueKind == JsonValueKind.True;
                buildSpec = new ContainerPluginBuildSpec { Context = context!, Dockerfile = dockerfile, Args = buildArgs, Push = push };
            }
        }

        ContainerPluginWorkspaceSpec? workspaceSpec = null;
        if (spec.TryGetProperty("workspace", out var wsEl) && wsEl.ValueKind == JsonValueKind.Object)
        {
            var wsPath = wsEl.TryGetProperty("path", out var wpEl) ? wpEl.GetString() ?? "/workspace" : "/workspace";
            var wsSize = wsEl.TryGetProperty("sizeMb", out var wsmEl) && wsmEl.ValueKind == JsonValueKind.Number ? wsmEl.GetInt32() : 0;
            var wsMedium = wsEl.TryGetProperty("medium", out var wmEl) ? wmEl.GetString() ?? "disk" : "disk";
            var wsPersist = wsEl.TryGetProperty("persist", out var wpsEl) && wpsEl.ValueKind == JsonValueKind.True;
            workspaceSpec = new ContainerPluginWorkspaceSpec { Path = wsPath, SizeMb = wsSize, Medium = wsMedium, Persist = wsPersist };
        }

        if (string.IsNullOrEmpty(image)) return null;

        return new ContainerPluginManifest(id!, version!, description, labels)
        {
            Spec = new ContainerPluginSpec
            {
                Image = image!,
                Build = buildSpec,
                Port = port,
                Topology = topology,
                StartupTimeoutSeconds = startupTimeout,
                InvokeTimeoutSeconds = invokeTimeout,
                SessionTtlSeconds = sessionTtl,
                InvokeIdleTimeoutSeconds = invokeIdleTimeout,
                ImagePullPolicy = imagePullPolicy,
                RetryPolicy = retryPolicy,
                Kubernetes = k8sConfig,
                Secrets = secrets,
                Workspace = workspaceSpec,
            },
        };
    }

    private static PluginManifest? ParsePlugin(JsonElement root, List<string> errors, string prefix)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid metadata block");
            return null;
        }

        var id = metadata.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var version = metadata.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
        if (string.IsNullOrEmpty(version)) version = "1.0";

        var description = metadata.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;

        if (string.IsNullOrEmpty(id))
            errors.Add($"{prefix}metadata.id is required");
        else if (!ManifestValidation.IsValidId(id))
            errors.Add($"{prefix}metadata.id '{id}' does not match ^[a-z][a-z0-9-]{{0,62}}$");

        var labels = ParseStringMap(metadata, "labels");

        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid spec block");
            return null;
        }

        if (string.IsNullOrEmpty(id)) return null;

        var language = spec.TryGetProperty("language", out var langEl) ? langEl.GetString() : null;
        if (string.IsNullOrEmpty(language))
            errors.Add($"{prefix}spec.language is required ('csharp' or 'python')");

        IReadOnlyList<PluginHandlerRef>? handlers = null;
        if (spec.TryGetProperty("handlers", out var handlersEl) && handlersEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<PluginHandlerRef>();
            var idx = 0;
            foreach (var item in handlersEl.EnumerateArray())
            {
                var itemPrefix = $"{prefix}spec.handlers[{idx}] ";
                var typeName = item.TryGetProperty("typeName", out var tnEl) ? tnEl.GetString() : null;
                if (string.IsNullOrEmpty(typeName))
                    errors.Add($"{itemPrefix}typeName is required");
                else
                    list.Add(new PluginHandlerRef(typeName!, item.TryGetProperty("assemblyName", out var anEl) ? anEl.GetString() : null));
                idx++;
            }
            if (list.Count > 0) handlers = list;
        }

        if (string.IsNullOrEmpty(language)) return null;

        return new PluginManifest(id!, version!, description, labels)
        {
            Spec = new PluginManifestSpec
            {
                Language = language!,
                Handlers = handlers,
            },
        };
    }

    private static ExtensionManifest? ParseExtension(JsonElement root, List<string> errors, string prefix)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid metadata block");
            return null;
        }

        // Extension manifests use 'name' (K8s-like); fall back to 'id' for compatibility.
        var id = (metadata.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null)
              ?? (metadata.TryGetProperty("id", out var idEl) ? idEl.GetString() : null);
        var version = metadata.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
        if (string.IsNullOrEmpty(version)) version = "0.0.0";

        var description = metadata.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
        var labels = ParseStringMap(metadata, "labels");

        if (string.IsNullOrEmpty(id))
        {
            errors.Add($"{prefix}metadata.name (or metadata.id) is required");
            return null;
        }

        if (!root.TryGetProperty("spec", out var spec) || spec.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid spec block");
            return null;
        }

        var host = spec.TryGetProperty("host", out var hostEl) ? hostEl.GetString() : null;
        if (string.IsNullOrEmpty(host))
        {
            errors.Add($"{prefix}spec.host is required ('csharp' or 'container')");
            return null;
        }

        var handlers = new List<ExtensionHandler>();
        if (spec.TryGetProperty("handlers", out var handlersEl) && handlersEl.ValueKind == JsonValueKind.Array)
        {
            var idx = 0;
            foreach (var item in handlersEl.EnumerateArray())
            {
                var hp = $"{prefix}spec.handlers[{idx}] ";
                var hid = item.TryGetProperty("id", out var hidEl) ? hidEl.GetString() : null;
                var seam = item.TryGetProperty("seam", out var seamEl) ? seamEl.GetString() : null;
                if (string.IsNullOrEmpty(hid)) errors.Add($"{hp}id is required");
                if (string.IsNullOrEmpty(seam)) errors.Add($"{hp}seam is required");
                var priority = item.TryGetProperty("priority", out var priEl) && priEl.TryGetInt32(out var priVal)
                    ? priVal : 100;
                var failureMode = item.TryGetProperty("failureMode", out var fmEl) ? fmEl.GetString() ?? "fail" : "fail";
                var typeName = item.TryGetProperty("typeName", out var tnEl) ? tnEl.GetString() : null;
                var endpoint = item.TryGetProperty("endpoint", out var epEl) ? epEl.GetString() : null;
                int? handlerTimeout = item.TryGetProperty("timeoutSeconds", out var htEl) && htEl.TryGetInt32(out var htVal)
                    ? htVal : null;
                if (!string.IsNullOrEmpty(hid) && !string.IsNullOrEmpty(seam))
                {
                    handlers.Add(new ExtensionHandler
                    {
                        Id = hid!,
                        Seam = seam!,
                        TypeName = typeName,
                        Endpoint = endpoint,
                        Priority = priority,
                        FailureMode = failureMode,
                        TimeoutSeconds = handlerTimeout,
                    });
                }
                idx++;
            }
        }

        if (handlers.Count == 0)
        {
            errors.Add($"{prefix}spec.handlers must contain at least one handler");
            return null;
        }

        return new ExtensionManifest(
            Id: id!,
            Version: version!,
            Spec: new ExtensionSpec
            {
                Host = host!,
                Package = spec.TryGetProperty("package", out var pkgEl) ? pkgEl.GetString() : null,
                Image = spec.TryGetProperty("image", out var imgEl) ? imgEl.GetString() : null,
                Port = spec.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out var portVal) ? portVal : null,
                Topology = spec.TryGetProperty("topology", out var topoEl) ? topoEl.GetString() : null,
                StartupTimeoutSeconds = spec.TryGetProperty("startupTimeoutSeconds", out var stEl) && stEl.TryGetInt32(out var stVal) ? stVal : null,
                InvokeTimeoutSeconds = spec.TryGetProperty("invokeTimeoutSeconds", out var itEl) && itEl.TryGetInt32(out var itVal) ? itVal : null,
                ImagePullPolicy = spec.TryGetProperty("imagePullPolicy", out var ippEl) ? ippEl.GetString() : null,
                Handlers = handlers,
                Scope = ParseExtensionScope(spec, prefix),
                Secrets = ParseStringMap(spec, "secrets"),
            },
            Labels: labels,
            Description: description);
    }

    private static ExtensionScope? ParseExtensionScope(JsonElement spec, string prefix)
    {
        if (!spec.TryGetProperty("scope", out var scopeEl) || scopeEl.ValueKind != JsonValueKind.Object)
            return null;

        IReadOnlyList<string>? workspaces = null;
        if (scopeEl.TryGetProperty("workspaces", out var wsEl) && wsEl.ValueKind == JsonValueKind.Array)
            workspaces = [.. wsEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0)];

        IReadOnlyList<string>? agentIds = null;
        if (scopeEl.TryGetProperty("agentIds", out var aidEl) && aidEl.ValueKind == JsonValueKind.Array)
            agentIds = [.. aidEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0)];

        LabelSelector? selector = null;
        if (scopeEl.TryGetProperty("selector", out var selEl) && selEl.ValueKind == JsonValueKind.Object)
        {
            var labels = selEl.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
            if (labels.Count > 0)
                selector = new LabelSelector(labels);
        }

        if (workspaces is null && agentIds is null && selector is null)
            return null;

        return new ExtensionScope(workspaces, agentIds, selector);
    }
}
