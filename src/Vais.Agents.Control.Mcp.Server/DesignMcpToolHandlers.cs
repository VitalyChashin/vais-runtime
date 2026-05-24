// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Vais.Agents.Control.Mcp.Server;

/// <summary>
/// MCP tool + resource handlers for the design-tools server. These are the read-only
/// verbs exposed at <c>/design-mcp</c>; mutation is out of scope for Plan A.
/// </summary>
internal static class DesignMcpToolHandlers
{
    // ── Tool list ─────────────────────────────────────────────────────────────

    internal static readonly IReadOnlyList<Tool> DesignTools =
    [
        new Tool
        {
            Name = "vais.list",
            Title = "List resources",
            Description = "List all registered resources of a given kind (Agent, AgentGraph, McpServer, LlmGatewayConfig, McpGatewayConfig, ContainerPlugin, EvalSuite).",
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
            Description = "Return the full manifest for a specific registered resource by kind + name (+ optional version).",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "kind":    { "type": "string", "description": "Resource kind." },
                    "name":    { "type": "string", "description": "Resource name (manifest metadata.name)." },
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
            Description = "Parse a candidate manifest YAML/JSON and diff it against the currently registered version. Returns field-level additions, removals, and changes. No resource is created or modified.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "manifest": {
                      "type": "string",
                      "description": "Full manifest content (YAML or JSON envelope with apiVersion/kind/metadata/spec)."
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
                      "description": "Full manifest content (YAML or JSON envelope with apiVersion/kind/metadata/spec)."
                    }
                  },
                  "required": ["manifest"]
                }
                """),
        },
    ];

    internal static ValueTask<ListToolsResult> HandleListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx,
        CancellationToken ct)
        => new(new ListToolsResult { Tools = [.. DesignTools] });

    // Tool call handler — implementations land in ND-6 / ND-7.
    internal static ValueTask<CallToolResult> HandleCallToolAsync(
        RequestContext<CallToolRequestParams> ctx,
        CancellationToken ct)
    {
        var name = ctx.Params?.Name ?? string.Empty;
        return new(TextError($"Tool '{name}' is registered but not yet implemented in this build (ND-6/ND-7 pending)."));
    }

    // ── Resource list ─────────────────────────────────────────────────────────

    internal static ValueTask<ListResourcesResult> HandleListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx,
        CancellationToken ct)
    {
        // Ontology resources — full list lands in ND-8.
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
