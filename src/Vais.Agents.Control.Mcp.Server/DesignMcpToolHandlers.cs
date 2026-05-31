// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Vais.Agents.Control.Manifests;
using Vais.Agents.Control.Mcp.Server.Ontology;
using Vais.Agents.Eval;

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
                      "enum": ["Agent", "AgentGraph", "McpServer", "LlmGatewayConfig", "McpGatewayConfig", "ContainerPlugin", "EvalSuite"],
                      "description": "Resource kind (case-insensitive; canonical values listed in enum)."
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
                    "kind":    { "type": "string", "enum": ["Agent", "AgentGraph", "McpServer", "LlmGatewayConfig", "McpGatewayConfig", "ContainerPlugin", "EvalSuite"], "description": "Resource kind (case-insensitive)." },
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
                    "kind": { "type": "string", "enum": ["Agent", "AgentGraph", "McpServer", "LlmGatewayConfig", "McpGatewayConfig", "ContainerPlugin", "EvalSuite"], "description": "Resource kind to describe (case-insensitive)." }
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
        new Tool
        {
            Name = "vais.diagnose",
            Title = "Diagnose a run",
            Description = "Return the mechanical-failure health rollup for a graph run: worst level (healthy|degraded|failed) plus every attributed failure signal across the run tree and background sub-runs. Each signal carries the ontology concept name (e.g. McpToolError) and the deployment attribution path (e.g. confluence-agent/confluence-mcp/confluence_search). Requires the run-health aggregator to be configured (VAIS_RUN_HEALTH_STORE_CONNECTION).",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "runId": {
                      "type": "string",
                      "description": "Root run id whose tree to diagnose. Sub-run trees are folded in automatically via the {parent}__{name}__{hash} convention."
                    }
                  },
                  "required": ["runId"]
                }
                """),
        },
        new Tool
        {
            Name = "vais.runHealth",
            Title = "Recent degraded/failed runs",
            Description = "Cross-run rollup: list recent runs whose worst mechanical-failure level is at least the requested minimum. Returns lightweight summaries { runId, level, signalCount, latestAt } for at-a-glance investigation. v1 indexes only bus-sourced signals (runs whose only failures came from the MCP gateway / LLM gateway / graph node stores are NOT yet enumerated cross-run — use vais.diagnose per-run for those).",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "level": { "type": "string", "enum": ["degraded", "failed"], "description": "Minimum severity to include. Default: degraded." },
                    "since": { "type": "string", "description": "ISO-8601 timestamp; default: 24h ago." },
                    "limit": { "type": "integer", "description": "Maximum runs to return (default 50, max 200).", "minimum": 1, "maximum": 200 }
                  }
                }
                """),
        },
        new Tool
        {
            Name = "vais.failures",
            Title = "Search failures across runs",
            Description = "Cross-run search for mechanical-failure signals. Powers the diagnose loop's \"is this a one-off or a pattern?\" question. Returns the most recent matching signals as { runId, conceptName, attributionPath, source, errorType, at }. v1 indexes bus-sourced concepts (ToolError, TurnFailed, PluginPartial, LlmCallRetried, LlmFallbackEngaged, GuardrailTriggered) via the run-health store AND McpToolError via the MCP gateway event store. NodeFailed / LlmCallFailure (plugin-path) / background failures are NOT yet indexed cross-run; only per-run via vais.diagnose.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "concept":  { "type": "string", "description": "Failure concept to match (e.g. \"McpToolError\" or \"ToolError\"). Sub-concepts via parent-walk. Null returns all indexed concepts." },
                    "agentName": { "type": "string", "description": "Filter to a specific source agent name (run-health store only)." },
                    "since":    { "type": "string", "description": "ISO-8601 timestamp; only signals at or after this time are returned. Default: 24h ago." },
                    "limit":    { "type": "integer", "description": "Maximum signals to return (default 50, max 200).", "minimum": 1, "maximum": 200 }
                  }
                }
                """),
        },
    ];

    // ── Mutating tool declarations (NB-9, NB-10) ──────────────────────────────

    internal static readonly IReadOnlyList<Tool> MutatingTools =
    [
        new Tool
        {
            Name = "vais.apply",
            Title = "Apply manifest",
            Description = "Create or update a resource from a manifest envelope (Agent, AgentGraph, McpServer, McpGatewayConfig, LlmGatewayConfig, ContainerPlugin). Validates first; on success returns { ok:true, kind, name, version, action }. A high-risk kind (ContainerPlugin) may return { ok:false, status:'pending-approval', requestId } — re-apply after an operator approves. Denied callers get { ok:false, denied:true, reason }.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "manifest": { "type": "string", "description": "Full manifest content as a JSON v0.6 envelope with apiVersion/kind/metadata/spec." }
                  },
                  "required": ["manifest"]
                }
                """),
        },
        new Tool
        {
            Name = "vais.delete",
            Title = "Delete resource",
            Description = "Delete a registered resource by kind + name (+ optional version). Returns { ok:true, action:'deleted' } or { ok:false, error }. Authorization is enforced; an unauthorized caller gets { ok:false, denied:true }.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "kind":    { "type": "string", "enum": ["Agent", "AgentGraph", "McpServer", "LlmGatewayConfig", "McpGatewayConfig", "ContainerPlugin"], "description": "Resource kind (case-insensitive)." },
                    "name":    { "type": "string", "description": "Resource name (manifest metadata.id)." },
                    "version": { "type": "string", "description": "Optional version. Omit for the latest." }
                  },
                  "required": ["kind", "name"]
                }
                """),
        },
        new Tool
        {
            Name = "vais.eval",
            Title = "Run an eval suite",
            Description = "Start an eval run to verify behavior — close the author→apply→verify loop. Provide either an inline EvalSuite manifest ('suite') or the name of a registered suite ('suiteRef'). Returns { ok:true, runId } immediately; poll vais.eval.status. An inline suite is authored first (requires author scope for EvalSuite).",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "suite":    { "type": "string", "description": "Inline EvalSuite manifest as a JSON v0.6 envelope. Mutually exclusive with suiteRef." },
                    "suiteRef": { "type": "string", "description": "Name (metadata.id) of an already-registered EvalSuite. Mutually exclusive with suite." }
                  }
                }
                """),
        },
        new Tool
        {
            Name = "vais.eval.status",
            Title = "Eval run status",
            Description = "Poll an eval run started by vais.eval. Returns { ok:true, status, totalCases, passedCases, failedCases, cases[] } or { ok:false, error } for an unknown runId.",
            InputSchema = ParseSchema("""
                {
                  "type": "object",
                  "properties": {
                    "runId": { "type": "string", "description": "Eval run id returned by vais.eval." }
                  },
                  "required": ["runId"]
                }
                """),
        },
    ];

    // ── List-tools (ND-5 + NB-12 scope filter) ────────────────────────────────

    internal static ValueTask<ListToolsResult> HandleListToolsAsync(
        RequestContext<ListToolsRequestParams> ctx,
        CancellationToken ct)
        => ListToolsAsync(ctx.Services!, ct);

    /// <summary>
    /// Testable overload — composes the substrate <c>tools/list</c> interception chain over a
    /// read-only baseline (<see cref="DesignTools"/>). The built-in
    /// <see cref="DesignToolsScopeFilterInterceptor"/> always runs (preserving Plan B byte
    /// parity); deployer-registered <c>OntologyInterceptor&lt;DesignToolsListInterceptionContext, ListToolsResult&gt;</c>
    /// implementations layer around it via DI.
    /// </summary>
    internal static async ValueTask<ListToolsResult> ListToolsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var deployer = sp.GetServices<OntologyInterceptor<DesignToolsListInterceptionContext, ListToolsResult>>();
        var builtIn = new DesignToolsScopeFilterInterceptor(
            sp.GetService<IAgentPolicyEngine>(),
            sp.GetService<IAgentContextAccessor>());
        var interceptors = new List<OntologyInterceptor<DesignToolsListInterceptionContext, ListToolsResult>>(deployer)
        {
            builtIn,
        };

        var context = new DesignToolsListInterceptionContext
        {
            Operation = OntologyOperation.List,
            AgentContext = sp.GetService<IAgentContextAccessor>()?.Current ?? AgentContext.Empty,
        };
        var chain = OntologyInterceptorChain.Compose(
            interceptors, context,
            terminal: () => Task.FromResult(new ListToolsResult { Tools = [.. DesignTools] }),
            cancellationToken: ct);
        return await chain().ConfigureAwait(false);
    }

    private static AgentPrincipal? BuildPrincipal(IServiceProvider sp)
    {
        var ctx = sp.GetService<IAgentContextAccessor>()?.Current ?? AgentContext.Empty;
        return ctx.UserId is { Length: > 0 } userId ? new AgentPrincipal(userId, ctx.TenantId, ctx.Scopes) : null;
    }

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
                "vais.apply"    => await HandleApplyAsync(args, sp, ct).ConfigureAwait(false),
                "vais.delete"   => await HandleDeleteAsync(args, sp, ct).ConfigureAwait(false),
                "vais.eval"     => await HandleEvalAsync(args, sp, ct).ConfigureAwait(false),
                "vais.eval.status" => await HandleEvalStatusAsync(args, sp, ct).ConfigureAwait(false),
                "vais.diagnose" => await HandleDiagnoseAsync(args, sp, ct).ConfigureAwait(false),
                "vais.runHealth" => await HandleRunHealthAsync(args, sp, ct).ConfigureAwait(false),
                "vais.failures" => await HandleFailuresAsync(args, sp, ct).ConfigureAwait(false),
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
        kind = DesignRegistryRouter.Normalize(kind)!;

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
        kind = DesignRegistryRouter.Normalize(kind) ?? kind;

        var catalog = sp.GetRequiredService<IOntologyCatalog>();
        if (!catalog.TryGet(kind, out var entry))
            return TextError($"Kind '{kind}' is not in the ontology catalog.");

        return TextSuccess(BuildOntologyJson(entry).ToJsonString());
    }

    private static JsonObject BuildOntologyJson(KindOntologyEntry entry)
    {
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
        return result;
    }

    // ── Failure taxonomy projection (Part 2c — DM-6) ──────────────────────────

    /// <summary>Renders the full failure taxonomy as a single JSON object.</summary>
    internal static JsonObject BuildFailureCatalogJson(IFailureOntologyCatalog catalog) => new()
    {
        ["ontologyVersion"] = catalog.OntologyVersion,
        ["concepts"] = new JsonArray([.. catalog.Concepts.Select(c => (JsonNode?)BuildFailureConceptJsonCore(c))]),
    };

    /// <summary>Renders a single failure concept as JSON, including its children (sub-concepts).</summary>
    internal static JsonObject BuildFailureConceptJson(
        IFailureOntologyCatalog catalog, string conceptName, string uriForError)
    {
        var concept = catalog.Get(conceptName)
            ?? throw new ArgumentException(
                $"Concept '{conceptName}' not found in the failure taxonomy.", nameof(uriForError));
        var node = BuildFailureConceptJsonCore(concept);

        // Add the children (concepts whose ParentName == this concept) so the resource is
        // self-contained — a caller can navigate the taxonomy without reading the full catalog.
        var children = catalog.Concepts
            .Where(c => string.Equals(c.ParentName, conceptName, StringComparison.Ordinal))
            .Select(c => c.Name)
            .ToList();
        node["children"] = new JsonArray([.. children.Select(n => (JsonNode?)JsonValue.Create(n))]);

        // Part 3 FP-11 read path: include inducted failure priors for this concept.
        var priors = catalog.GetPriorsForConcept(conceptName);
        if (priors.Count > 0)
        {
            node["priors"] = new JsonArray([.. priors.Select(p => (JsonNode?)new JsonObject
            {
                ["attributionPath"] = p.AttributionPath,
                ["agentName"] = p.Prior.AgentName,
                ["toolName"] = p.Prior.ToolName,
                ["failureCount"] = p.Prior.FailureCount,
                ["firstSeen"] = p.Prior.FirstSeen.ToString("o"),
                ["lastSeen"] = p.Prior.LastSeen.ToString("o"),
            })]);
        }

        return node;
    }

    private static JsonObject BuildFailureConceptJsonCore(FailureConcept c)
    {
        var obj = new JsonObject
        {
            ["name"] = c.Name,
            ["axis"] = c.Axis.ToString(),
            ["defaultLevel"] = c.DefaultLevel.ToString(),
            ["description"] = c.Description,
            ["parentName"] = c.ParentName,
            ["sourceKinds"] = new JsonArray([.. c.SourceKinds.Select(k => (JsonNode?)JsonValue.Create(k.ToString()))]),
        };
        if (c.Tags is { Count: > 0 })
            obj["tags"] = new JsonArray([.. c.Tags.Select(t => (JsonNode?)JsonValue.Create(t))]);
        return obj;
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

    private static async ValueTask<CallToolResult> HandleValidateAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var manifest = GetString(args, "manifest");
        if (string.IsNullOrWhiteSpace(manifest))
            return TextError("Missing required argument 'manifest'.");

        var outcome = await RunValidationChainAsync(manifest, sp, ct).ConfigureAwait(false);
        var result = new JsonObject
        {
            ["ok"] = outcome.Ok,
            ["errors"] = new JsonArray(outcome.Errors.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
            ["suggestions"] = new JsonArray(outcome.Suggestions.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };
        return TextSuccess(result.ToJsonString());
    }

    /// <summary>
    /// Compose and run the substrate <c>vais.validate</c> interception chain. The built-in
    /// <see cref="ManifestValidatorInterceptor"/> always runs (schema + cross-ref integrity,
    /// byte-parity with Plan A); deployer-registered
    /// <c>OntologyInterceptor&lt;DesignValidateInterceptionContext, ValidationOutcome&gt;</c>
    /// implementations layer around it via DI.
    /// </summary>
    internal static async Task<ValidationOutcome> RunValidationChainAsync(
        string manifestJson, IServiceProvider sp, CancellationToken ct)
    {
        var deployer = sp.GetServices<OntologyInterceptor<DesignValidateInterceptionContext, ValidationOutcome>>();
        var builtIn = new ManifestValidatorInterceptor(sp);
        var interceptors = new List<OntologyInterceptor<DesignValidateInterceptionContext, ValidationOutcome>>(deployer)
        {
            builtIn,
        };

        var context = new DesignValidateInterceptionContext
        {
            Operation = OntologyOperation.Call,
            AgentContext = sp.GetService<IAgentContextAccessor>()?.Current ?? AgentContext.Empty,
            ManifestJson = manifestJson,
        };
        var chain = OntologyInterceptorChain.Compose(
            interceptors, context,
            terminal: () => Task.FromResult(ValidationOutcome.AllOk),
            cancellationToken: ct);
        return await chain().ConfigureAwait(false);
    }

    // ── vais.eval + vais.eval.status — NB-11 ──────────────────────────────────

    private static async ValueTask<CallToolResult> HandleEvalAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var manager = sp.GetService<IEvalRunLifecycleManager>();
        if (manager is null) return TextError("Eval runs are not enabled on this runtime.");

        var inline = GetString(args, "suite");
        var suiteRef = GetString(args, "suiteRef");

        string suiteName;
        if (!string.IsNullOrWhiteSpace(inline))
        {
            // Inline suite: parse → RBAC-gate (EvalSuiteUpsert) → register, so the run can resolve it by name.
            EvalSuiteManifest suite;
            try
            {
                var resources = await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(inline, ct).ConfigureAwait(false);
                var found = resources.OfType<ManifestResource.EvalSuiteCase>().Select(c => c.Suite).FirstOrDefault();
                if (found is null)
                    return TextSuccess(new JsonObject { ["ok"] = false, ["error"] = "inline 'suite' must be a single EvalSuite manifest." }.ToJsonString());
                suite = found;
            }
            catch (Exception ex) when (ex is AgentManifestValidationException or JsonException)
            {
                return TextSuccess(new JsonObject { ["ok"] = false, ["error"] = $"suite parse failed: {ex.Message}" }.ToJsonString());
            }

            var principal = BuildPrincipal(sp);
            var policy = sp.GetService<IAgentPolicyEngine>() ?? NullAgentPolicyEngine.Instance;
            var decision = await policy.EvaluateAsync(PolicyOperation.EvalSuiteUpsert, manifest: null, principal, ct).ConfigureAwait(false);
            await AuditEvalUpsertAsync(sp, suite, principal, decision).ConfigureAwait(false);
            if (!decision.IsAllowed)
                return TextSuccess(new JsonObject { ["ok"] = false, ["denied"] = true, ["reason"] = decision.Reason ?? "policy denied" }.ToJsonString());

            await sp.GetRequiredService<IEvalSuiteRegistry>().UpsertAsync(suite, ct).ConfigureAwait(false);
            suiteName = suite.Id;
        }
        else if (!string.IsNullOrWhiteSpace(suiteRef))
        {
            suiteName = suiteRef;
        }
        else
        {
            return TextError("Provide either 'suite' (inline EvalSuite manifest) or 'suiteRef' (registered suite name).");
        }

        var workspace = (sp.GetService<IAgentContextAccessor>()?.Current.WorkspaceId) ?? "default";
        try
        {
            var runId = await manager.StartRunAsync(suiteName, workspace, ct).ConfigureAwait(false);
            return TextSuccess(new JsonObject { ["ok"] = true, ["runId"] = runId, ["suite"] = suiteName }.ToJsonString());
        }
        catch (KeyNotFoundException)
        {
            return TextSuccess(new JsonObject { ["ok"] = false, ["error"] = $"eval-suite '{suiteName}' is not registered." }.ToJsonString());
        }
    }

    private static async ValueTask<CallToolResult> HandleEvalStatusAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var runId = GetString(args, "runId");
        if (string.IsNullOrWhiteSpace(runId)) return TextError("Missing required argument 'runId'.");

        var manager = sp.GetService<IEvalRunLifecycleManager>();
        if (manager is null) return TextError("Eval runs are not enabled on this runtime.");

        var detail = await manager.GetRunDetailAsync(runId, ct).ConfigureAwait(false);
        if (detail is null)
            return TextSuccess(new JsonObject { ["ok"] = false, ["error"] = $"eval run '{runId}' not found." }.ToJsonString());

        var s = detail.Summary;
        var cases = new JsonArray(detail.Cases
            .Select(c => (JsonNode?)new JsonObject { ["caseId"] = c.CaseId, ["status"] = c.Status.ToString() })
            .ToArray());
        return TextSuccess(new JsonObject
        {
            ["ok"] = true,
            ["runId"] = s.EvalRunId,
            ["suite"] = s.SuiteName,
            ["status"] = s.Status.ToString(),
            ["totalCases"] = s.TotalCases,
            ["passedCases"] = s.PassedCases,
            ["failedCases"] = s.FailedCases,
            ["completedAt"] = s.CompletedAt?.ToString("o"),
            ["cases"] = cases,
        }.ToJsonString());
    }

    private static async ValueTask AuditEvalUpsertAsync(IServiceProvider sp, EvalSuiteManifest suite, AgentPrincipal? principal, PolicyDecision decision)
    {
        var audit = sp.GetService<IAuditLog>() ?? NullAuditLog.Instance;
        try
        {
            await audit.AppendAsync(new AuditLogEntry(
                At: DateTimeOffset.UtcNow,
                Operation: PolicyOperation.EvalSuiteUpsert,
                AgentId: suite.Id,
                AgentVersion: suite.Version,
                PrincipalId: principal?.Id ?? "anonymous",
                TenantId: principal?.TenantId,
                Allowed: decision.IsAllowed,
                DenyReason: decision.IsAllowed ? null : (decision.Reason ?? "policy denied"),
                ErrorType: null)).ConfigureAwait(false);
        }
        catch
        {
            // Audit-write failures must not break the verb.
        }
    }

    // ── vais.apply — NB-9 ─────────────────────────────────────────────────────

    private static async ValueTask<CallToolResult> HandleApplyAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var manifest = GetString(args, "manifest");
        if (string.IsNullOrWhiteSpace(manifest))
            return TextError("Missing required argument 'manifest'.");

        // Pre-apply: schema + cross-ref validation so the agent gets suggestions before the seam.
        var (ok, errors, suggestions) = await ManifestValidator.ValidateAsync(manifest, sp, ct).ConfigureAwait(false);
        if (!ok)
        {
            var bad = new JsonObject
            {
                ["ok"] = false,
                ["errors"] = StrArray(errors),
                ["suggestions"] = StrArray(suggestions),
            };
            return TextSuccess(bad.ToJsonString());
        }

        try
        {
            var result = await DesignMutationRouter.ApplyAsync(manifest, sp, ct).ConfigureAwait(false);
            return TextSuccess(result.ToJsonString());
        }
        catch (ApprovalRequiredException are)
        {
            return TextSuccess(new JsonObject
            {
                ["ok"] = false,
                ["approvalStatus"] = "pending-approval",
                ["requestId"] = are.RequestId,
                ["kind"] = are.Kind,
                ["name"] = are.Name,
            }.ToJsonString());
        }
        catch (AgentPolicyDeniedException pd)
        {
            return TextSuccess(new JsonObject { ["ok"] = false, ["denied"] = true, ["reason"] = pd.Message }.ToJsonString());
        }
    }

    // ── vais.delete — NB-10 ───────────────────────────────────────────────────

    private static async ValueTask<CallToolResult> HandleDeleteAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var kind = GetString(args, "kind");
        var name = GetString(args, "name");
        var version = GetString(args, "version");
        if (string.IsNullOrWhiteSpace(kind)) return TextError("Missing required argument 'kind'.");
        if (string.IsNullOrWhiteSpace(name)) return TextError("Missing required argument 'name'.");

        try
        {
            var result = await DesignMutationRouter.DeleteAsync(kind, name, version, sp, ct).ConfigureAwait(false);
            return TextSuccess(result.ToJsonString());
        }
        catch (ApprovalRequiredException are)
        {
            return TextSuccess(new JsonObject
            {
                ["ok"] = false,
                ["approvalStatus"] = "pending-approval",
                ["requestId"] = are.RequestId,
                ["kind"] = are.Kind,
                ["name"] = are.Name,
            }.ToJsonString());
        }
        catch (AgentPolicyDeniedException pd)
        {
            return TextSuccess(new JsonObject { ["ok"] = false, ["denied"] = true, ["reason"] = pd.Message }.ToJsonString());
        }
    }

    private static JsonArray StrArray(IEnumerable<string> items)
        => new(items.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());

    // ── vais.diagnose (Part 2c — DM-2) ─────────────────────────────────────────

    /// <summary>
    /// Returns the per-run mechanical-failure health rollup. Reshapes the domain
    /// <see cref="RunHealth"/> directly into MCP JSON so <c>ConceptName</c> and
    /// <c>AttributionPath</c> from Parts 2a/2b reach the caller — the REST DTO path
    /// (<c>RunHealthSignalDto</c>) is bypassed deliberately to avoid any silent drop
    /// if its projection ever drifts again.
    /// </summary>
    private static async ValueTask<CallToolResult> HandleDiagnoseAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var runId = GetString(args, "runId");
        if (string.IsNullOrWhiteSpace(runId))
            return TextError("Missing required argument 'runId'.");

        var aggregator = sp.GetService<IRunHealthAggregator>();
        if (aggregator is null)
            return TextError("Diagnose unavailable: IRunHealthAggregator is not registered (set VAIS_RUN_HEALTH_STORE_CONNECTION).");

        var health = await aggregator.GetRunHealthAsync(runId, ct).ConfigureAwait(false);
        return TextSuccess(BuildDiagnoseJson(health).ToJsonString());
    }

    /// <summary>
    /// Projects a domain <see cref="RunHealth"/> to the MCP JSON shape. Exposed for the
    /// <c>vais-diagnostics://run/{id}</c> resource handler so the resource and tool produce
    /// byte-identical bodies.
    /// </summary>
    internal static JsonObject BuildDiagnoseJson(RunHealth health) => new()
    {
        ["runId"] = health.RunId,
        ["level"] = health.Level.ToString().ToLowerInvariant(),
        ["signals"] = new JsonArray([.. health.Signals.Select(BuildSignalJson)]),
        ["backgroundFailures"] = new JsonArray([.. health.BackgroundFailures.Select(BuildSignalJson)]),
    };

    private static JsonNode BuildSignalJson(RunHealthSignal s) => new JsonObject
    {
        ["source"] = s.Source,
        ["kind"] = ToCamel(s.Kind.ToString()),
        ["level"] = s.Level.ToString().ToLowerInvariant(),
        ["errorType"] = s.ErrorType,
        ["isTransient"] = s.IsTransient,
        ["at"] = s.At.ToString("o"),
        ["conceptName"] = s.ConceptName,
        ["attributionPath"] = s.AttributionPath,
    };

    private static string ToCamel(string pascal) =>
        string.IsNullOrEmpty(pascal) ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];

    // ── vais.runHealth (Part 2c — DM-3) ────────────────────────────────────────

    private static async ValueTask<CallToolResult> HandleRunHealthAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var aggregator = sp.GetService<IRunHealthAggregator>();
        if (aggregator is null)
            return TextError("Run-health rollup unavailable: IRunHealthAggregator is not registered (set VAIS_RUN_HEALTH_STORE_CONNECTION).");

        var levelStr = GetString(args, "level");
        if (!string.IsNullOrEmpty(levelStr) && levelStr is not ("degraded" or "failed"))
            return TextError($"Argument 'level' must be 'degraded' or 'failed'; got '{levelStr}'.");
        var minLevel = levelStr switch
        {
            "failed" => FailureLevel.Error,
            _ => FailureLevel.Warning,  // null/empty/degraded
        };

        var sinceStr = GetString(args, "since");
        DateTimeOffset? since = null;
        if (!string.IsNullOrWhiteSpace(sinceStr))
        {
            if (!DateTimeOffset.TryParse(sinceStr, out var parsed))
                return TextError($"Argument 'since' must be a valid ISO-8601 timestamp; got '{sinceStr}'.");
            since = parsed;
        }
        var limit = 50;
        if (args?.TryGetValue("limit", out var limEl) == true && limEl.ValueKind == JsonValueKind.Number
            && limEl.TryGetInt32(out var limInt))
            limit = limInt;

        var rows = await aggregator.ListDegradedRunsAsync(minLevel, since, limit, ct).ConfigureAwait(false);
        var items = new JsonArray([.. rows.Select(BuildRunHealthRowJson)]);
        var payload = new JsonObject
        {
            ["count"] = rows.Count,
            ["items"] = items,
        };
        return TextSuccess(payload.ToJsonString());
    }

    private static JsonNode BuildRunHealthRowJson(RunHealthListItem r) => new JsonObject
    {
        ["runId"] = r.RunId,
        ["level"] = r.Level,
        ["signalCount"] = r.SignalCount,
        ["latestAt"] = r.LatestAt.ToString("o"),
    };

    // ── vais.failures (Part 2c — DM-4) ─────────────────────────────────────────

    private static async ValueTask<CallToolResult> HandleFailuresAsync(
        IDictionary<string, JsonElement>? args,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var search = sp.GetService<IFailureSearchService>();
        if (search is null)
            return TextError("Search unavailable: IFailureSearchService is not registered (set VAIS_RUN_HEALTH_STORE_CONNECTION).");

        var concept = GetString(args, "concept");
        var agentName = GetString(args, "agentName");
        var sinceStr = GetString(args, "since");
        DateTimeOffset? since = null;
        if (!string.IsNullOrWhiteSpace(sinceStr))
        {
            if (!DateTimeOffset.TryParse(sinceStr, out var parsed))
                return TextError($"Argument 'since' must be a valid ISO-8601 timestamp; got '{sinceStr}'.");
            since = parsed;
        }
        var limit = 50;
        if (args?.TryGetValue("limit", out var limEl) == true && limEl.ValueKind == JsonValueKind.Number
            && limEl.TryGetInt32(out var limInt))
            limit = limInt;

        var rows = await search.SearchAsync(
            new FailureSearchQuery(
                ConceptName: concept,
                AgentName: agentName,
                Since: since,
                Limit: limit),
            ct).ConfigureAwait(false);

        var items = new JsonArray([.. rows.Select(BuildFailureSearchRowJson)]);
        var payload = new JsonObject
        {
            ["count"] = rows.Count,
            ["items"] = items,
        };
        return TextSuccess(payload.ToJsonString());
    }

    private static JsonNode BuildFailureSearchRowJson(FailureSearchResult r) => new JsonObject
    {
        ["runId"] = r.RunId,
        ["conceptName"] = r.ConceptName,
        ["attributionPath"] = r.AttributionPath,
        ["source"] = r.Source,
        ["level"] = r.Level,
        ["errorType"] = r.ErrorType,
        ["at"] = r.At.ToString("o"),
    };

    // ── Resource handlers (ND-8) ──────────────────────────────────────────────

    internal static ValueTask<ListResourcesResult> HandleListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx,
        CancellationToken ct)
        => ListOntologyResourcesAsync(ctx.Services!);

    internal static ValueTask<ReadResourceResult> HandleReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx,
        CancellationToken ct)
    {
        var uri = ctx.Params?.Uri ?? string.Empty;
        // Part 2c (DM-7): per-run diagnostics resource — same payload as vais.diagnose.
        if (uri.StartsWith("vais-diagnostics://", StringComparison.OrdinalIgnoreCase))
            return ReadDiagnosticsResourceAsync(uri, ctx.Services!, ct);
        return ReadOntologyResourceAsync(uri, ctx.Services!);
    }

    /// <summary>Testable overload — returns all vais-ontology:// resources.</summary>
    internal static ValueTask<ListResourcesResult> ListOntologyResourcesAsync(IServiceProvider sp)
    {
        var catalog = sp.GetRequiredService<IOntologyCatalog>();
        var resources = catalog.Kinds
            .Select(kind =>
            {
                var entry = catalog.Get(kind);
                return new Resource
                {
                    Uri = $"vais-ontology://{kind}",
                    Name = kind,
                    Title = $"{kind} ontology",
                    Description = entry.Description,
                    MimeType = "application/json",
                };
            })
            .ToList();

        // Part 2c (DM-6): expose the failure taxonomy as a vais-ontology:// resource so
        // a coding agent can discover the concept vocabulary alongside the manifest kinds.
        var failureCatalog = sp.GetService<IFailureOntologyCatalog>();
        if (failureCatalog is not null)
        {
            resources.Add(new Resource
            {
                Uri = "vais-ontology://Failure",
                Name = "Failure",
                Title = "Failure ontology",
                Description = "Shared failure-concept vocabulary (mechanical + quality axes). Per-concept details under vais-ontology://Failure/{conceptName}.",
                MimeType = "application/json",
            });
        }
        return new(new ListResourcesResult { Resources = resources });
    }

    /// <summary>Testable overload — reads a single <c>vais-ontology://Kind</c> resource.</summary>
    internal static ValueTask<ReadResourceResult> ReadOntologyResourceAsync(string uri, IServiceProvider sp)
    {
        const string prefix = "vais-ontology://";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Unknown resource scheme in '{uri}'. Expected 'vais-ontology://<Kind>'.", nameof(uri));

        var path = uri[prefix.Length..].Trim('/');

        // Part 2c (DM-6): Failure taxonomy resource — root catalog + per-concept details.
        if (path.Equals("Failure", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Failure/", StringComparison.OrdinalIgnoreCase))
        {
            var failureCatalog = sp.GetService<IFailureOntologyCatalog>()
                ?? throw new ArgumentException(
                    $"Failure taxonomy resource '{uri}' requires IFailureOntologyCatalog (Part 2a) to be registered.", nameof(uri));
            var failureText = path.Length <= "Failure".Length
                ? BuildFailureCatalogJson(failureCatalog).ToJsonString()
                : BuildFailureConceptJson(failureCatalog, path["Failure/".Length..], uri).ToJsonString();
            return new(new ReadResourceResult
            {
                Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = failureText }],
            });
        }

        var catalog = sp.GetRequiredService<IOntologyCatalog>();
        if (!catalog.TryGet(path, out var entry))
            throw new ArgumentException($"Kind '{path}' not found in the ontology catalog.", nameof(uri));

        var text = BuildOntologyJson(entry).ToJsonString();
        return new(new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = text }],
        });
    }

    /// <summary>
    /// Part 2c (DM-7) — serves <c>vais-diagnostics://run/{runId}</c> with the same payload
    /// <c>vais.diagnose(runId)</c> returns. Exposed so a coding agent can pull a specific
    /// run's diagnostics via the resource API without a tool call.
    /// </summary>
    internal static async ValueTask<ReadResourceResult> ReadDiagnosticsResourceAsync(
        string uri, IServiceProvider sp, CancellationToken ct)
    {
        const string prefix = "vais-diagnostics://";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Unknown resource scheme in '{uri}'. Expected '{prefix}run/<runId>'.", nameof(uri));

        var path = uri[prefix.Length..].Trim('/');
        const string runPrefix = "run/";
        if (!path.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Unknown diagnostics resource '{uri}'. Expected '{prefix}run/<runId>'.", nameof(uri));

        var runId = path[runPrefix.Length..].Trim('/');
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException($"Missing run id in '{uri}'.", nameof(uri));

        var aggregator = sp.GetService<IRunHealthAggregator>()
            ?? throw new ArgumentException(
                $"Diagnostics resource '{uri}' requires IRunHealthAggregator to be registered (set VAIS_RUN_HEALTH_STORE_CONNECTION).", nameof(uri));

        var health = await aggregator.GetRunHealthAsync(runId, ct).ConfigureAwait(false);
        var text = BuildDiagnoseJson(health).ToJsonString();
        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = text }],
        };
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
