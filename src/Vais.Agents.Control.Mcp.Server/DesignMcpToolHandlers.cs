// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Control.Mcp.Server;

/// <summary>
/// MCP tool + resource handlers for the design-tools server. These are the read-only
/// verbs exposed at <c>/design-mcp</c>; mutation is out of scope for Plan A.
/// </summary>
internal static class DesignMcpToolHandlers
{
    // ── Tool declarations (ND-5) ──────────────────────────────────────────────

    internal static readonly IReadOnlyList<Tool> DesignTools =
    [
        new Tool
        {
            Name = "vais.list",
            Title = "List resources",
            Description = "List all registered resources of a given kind (Agent, AgentGraph, McpServer, LlmGatewayConfig, McpGatewayConfig, ContainerPlugin, EvalSuite). Returns an array of v0.6 envelope JSON objects.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "kind": {
                      "type": "string",
                      "description": "Resource kind — one of Agent, AgentGraph, McpServer, LlmGatewayConfig, McpGatewayConfig, ContainerPlugin, EvalSuite."
                    },
                    "labelSelector": {
                      "type": "string",
                      "description": "Optional label-selector prefix to filter results (e.g. 'team=payments')."
                    }
                  },
                  "required": ["kind"]
                }
                """),
        },
        new Tool
        {
            Name = "vais.get",
            Title = "Get resource",
            Description = "Return the full v0.6 envelope manifest for a specific registered resource by kind + name (+ optional version).",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "kind":    { "type": "string", "description": "Resource kind." },
                    "name":    { "type": "string", "description": "Resource name (manifest metadata.id)." },
                    "version": { "type": "string", "description": "Optional version. Omit for the latest." }
                  },
                  "required": ["kind", "name"]
                }
                """),
        },
        new Tool
        {
            Name = "vais.describe",
            Title = "Describe kind",
            Description = "Return ontology information for a kind: field types, required fields, cross-reference edges, capability/risk tags, and authoring recipes.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "kind": { "type": "string", "description": "Resource kind to describe." }
                  },
                  "required": ["kind"]
                }
                """),
        },
        new Tool
        {
            Name = "vais.diff",
            Title = "Diff manifest",
            Description = "Parse a candidate manifest JSON envelope and diff its spec against the currently registered version. Returns field-level additions, removals, and changes. No resource is created or modified.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "manifest": {
                      "type": "string",
                      "description": "Full manifest content as a JSON v0.6 envelope with apiVersion/kind/metadata/spec."
                    }
                  },
                  "required": ["manifest"]
                }
                """),
        },
        new Tool
        {
            Name = "vais.validate",
            Title = "Validate manifest (dry-run)",
            Description = "Dry-run validate a candidate manifest: JSON-Schema check + cross-reference integrity (dangling refs) + overlay author-policy read-check. Returns { ok, errors[], suggestions[] }. No resource is created or modified.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "manifest": {
                      "type": "string",
                      "description": "Full manifest content as a JSON v0.6 envelope with apiVersion/kind/metadata/spec."
                    }
                  },
                  "required": ["manifest"]
                }
                """),
        },
    ];

    // ── List-tools (ND-5) ─────────────────────────────────────────────────────

    internal static ValueTask<ListToolsResult> HandleListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx,
        CancellationToken ct)
        => new(new ListToolsResult { Tools = [.. DesignTools] });

    // ── Call-tool dispatcher (ND-6, ND-7) ────────────────────────────────────

    internal static ValueTask<CallToolResult> HandleCallToolAsync(
        RequestContext<CallToolRequestParams> ctx,
        CancellationToken ct)
        => InvokeAsync(ctx.Params?.Name ?? string.Empty, ctx.Params?.Arguments, ctx.Services!, ct);

    /// <summary>Testable overload — takes args + IServiceProvider directly.</summary>
    internal static async ValueTask<CallToolResult> InvokeAsync(
        string name,
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        try
        {
            return name switch
            {
                "vais.list"     => await HandleListAsync(args, sp, ct).ConfigureAwait(false),
                "vais.get"      => await HandleGetAsync(args, sp, ct).ConfigureAwait(false),
                "vais.describe" => HandleDescribe(args, sp),
                "vais.diff"     => await HandleDiffAsync(args, sp, ct).ConfigureAwait(false),
                "vais.validate" => await HandleValidateAsync(args, sp, ct).ConfigureAwait(false),
                _               => TextError($"Unknown design tool '{name}'."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TextError($"{name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── vais.list ─────────────────────────────────────────────────────────────

    private static async ValueTask<CallToolResult> HandleListAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var kind = GetString(args, "kind");
        if (string.IsNullOrWhiteSpace(kind))
            return TextError("Missing required argument 'kind'.");
        if (!DesignRegistryRouter.IsSupported(kind))
            return TextError($"Kind '{kind}' is not supported. Supported: Agent, AgentGraph, McpServer, LlmGatewayConfig, McpGatewayConfig, ContainerPlugin, EvalSuite.");

        var labelSelector = GetString(args, "labelSelector");
        var items = await DesignRegistryRouter.ListAsync(kind, sp, labelSelector, ct).ConfigureAwait(false);

        var array = new JsonArray(items.Select(j => (JsonNode?)JsonNode.Parse(j)).ToArray());
        var result = new JsonObject { ["kind"] = kind, ["items"] = array, ["count"] = items.Count };
        return TextSuccess(result.ToJsonString());
    }

    // ── vais.get ──────────────────────────────────────────────────────────────

    private static async ValueTask<CallToolResult> HandleGetAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var kind = GetString(args, "kind");
        var name = GetString(args, "name");
        var version = GetString(args, "version");
        if (string.IsNullOrWhiteSpace(kind)) return TextError("Missing required argument 'kind'.");
        if (string.IsNullOrWhiteSpace(name)) return TextError("Missing required argument 'name'.");
        if (!DesignRegistryRouter.IsSupported(kind))
            return TextError($"Kind '{kind}' is not supported.");

        var json = await DesignRegistryRouter.GetAsync(kind, name, version, sp, ct).ConfigureAwait(false);
        if (json is null)
            return TextError($"{kind}/{name}{(version is null ? "" : $"@{version}")} is not registered.");

        return TextSuccess(json);
    }

    // ── vais.describe ─────────────────────────────────────────────────────────

    private static CallToolResult HandleDescribe(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp)
    {
        var kind = GetString(args, "kind");
        if (string.IsNullOrWhiteSpace(kind))
            return TextError("Missing required argument 'kind'.");

        var catalog = sp.GetRequiredService<IOntologyCatalog>();
        if (!catalog.TryGet(kind, out var entry))
            return TextError($"Kind '{kind}' is not in the ontology catalog.");

        var fieldsObj = new JsonObject();
        foreach (var (fieldName, fi) in entry.Fields)
        {
            var fNode = new JsonObject
            {
                ["type"] = fi.Type,
                ["description"] = fi.Description,
            };
            if (fi.EnumValues is { Count: > 0 })
                fNode["enum"] = new JsonArray(fi.EnumValues.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            if (fi.Tags.Count > 0)
                fNode["tags"] = new JsonArray(fi.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
            fieldsObj[fieldName] = fNode;
        }

        var crossRefsArr = new JsonArray(
            entry.CrossRefs.Select(cr => (JsonNode?)new JsonObject
            {
                ["field"] = cr.FieldPath,
                ["targetKind"] = cr.TargetKind,
                ["cardinality"] = cr.Cardinality,
            }).ToArray());

        var tagsArr = new JsonArray(entry.Tags.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        var requiredArr = new JsonArray(entry.RequiredFields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());

        var recipesArr = new JsonArray(
            entry.Recipes.Select(r => (JsonNode?)new JsonObject
            {
                ["name"] = r.Name,
                ["description"] = r.Description,
            }).ToArray());

        var result = new JsonObject
        {
            ["kind"] = entry.Kind,
            ["ontologyVersion"] = entry.OntologyVersion,
            ["description"] = entry.Description,
            ["required"] = requiredArr,
            ["fields"] = fieldsObj,
            ["crossRefs"] = crossRefsArr,
            ["tags"] = tagsArr,
            ["recipes"] = recipesArr,
        };
        if (entry.ManualConcept is not null)
            result["manualConcept"] = entry.ManualConcept;

        return TextSuccess(result.ToJsonString());
    }

    // ── vais.diff ─────────────────────────────────────────────────────────────

    private static async ValueTask<CallToolResult> HandleDiffAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var manifest = GetString(args, "manifest");
        if (string.IsNullOrWhiteSpace(manifest))
            return TextError("Missing required argument 'manifest'.");

        var diff = await DesignRegistryRouter.DiffAsync(manifest, sp, ct).ConfigureAwait(false);
        return TextSuccess(diff.ToJsonString());
    }

    // ── vais.validate — ND-7 ─────────────────────────────────────────────────

    private static ValueTask<CallToolResult> HandleValidateAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var manifest = GetString(args, "manifest");
        if (string.IsNullOrWhiteSpace(manifest))
            return new(TextError("Missing required argument 'manifest'."));

        // Full implementation lands in ND-7 (cross-ref integrity + policy check).
        // For now: parse the envelope and confirm it is well-formed JSON with the right shape.
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            var root = doc.RootElement;
            var missingKeys = new List<string>(3);
            if (!root.TryGetProperty("kind", out _)) missingKeys.Add("kind");
            if (!root.TryGetProperty("metadata", out _)) missingKeys.Add("metadata");
            if (!root.TryGetProperty("spec", out _)) missingKeys.Add("spec");
            if (missingKeys.Count > 0)
            {
                var errNode = new JsonObject
                {
                    ["ok"] = false,
                    ["errors"] = new JsonArray(missingKeys.Select(k => (JsonNode?)JsonValue.Create($"Missing required envelope key: '{k}'")).ToArray()),
                    ["suggestions"] = new JsonArray(),
                };
                return new(TextSuccess(errNode.ToJsonString()));
            }

            var okNode = new JsonObject
            {
                ["ok"] = true,
                ["errors"] = new JsonArray(),
                ["suggestions"] = new JsonArray(
                    (JsonNode?)JsonValue.Create("Full cross-ref integrity check lands in ND-7.")),
            };
            return new(TextSuccess(okNode.ToJsonString()));
        }
        catch (JsonException ex)
        {
            var errNode = new JsonObject
            {
                ["ok"] = false,
                ["errors"] = new JsonArray((JsonNode?)JsonValue.Create($"Invalid JSON: {ex.Message}")),
                ["suggestions"] = new JsonArray(),
            };
            return new(TextSuccess(errNode.ToJsonString()));
        }
    }

    // ── Resource handlers (ND-8) ──────────────────────────────────────────────

    internal static ValueTask<ListResourcesResult> HandleListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx,
        CancellationToken ct)
    {
        // Full ontology resources land in ND-8.
        return new(new ListResourcesResult { Resources = [] });
    }

    internal static ValueTask<ReadResourceResult> HandleReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx,
        CancellationToken ct)
    {
        var uri = ctx.Params?.Uri ?? string.Empty;
        throw new ArgumentException($"Resource '{uri}' not found. Ontology resources land in ND-8.", nameof(ctx));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string? GetString(IDictionary<string, JsonElement>? args, string key)
        => args?.TryGetValue(key, out var v) == true && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    internal static CallToolResult TextError(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = true,
    };

    internal static CallToolResult TextSuccess(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = false,
    };
}
