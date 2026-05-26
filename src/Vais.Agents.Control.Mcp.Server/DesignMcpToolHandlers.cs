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
                ["status"] = "pending-approval",
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
                ["status"] = "pending-approval",
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

    // ── Resource handlers (ND-8) ──────────────────────────────────────────────

    internal static ValueTask<ListResourcesResult> HandleListResourcesAsync(
        RequestContext<ListResourcesRequestParams> ctx,
        CancellationToken ct)
        => ListOntologyResourcesAsync(ctx.Services!);

    internal static ValueTask<ReadResourceResult> HandleReadResourceAsync(
        RequestContext<ReadResourceRequestParams> ctx,
        CancellationToken ct)
        => ReadOntologyResourceAsync(ctx.Params?.Uri ?? string.Empty, ctx.Services!);

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
        return new(new ListResourcesResult { Resources = resources });
    }

    /// <summary>Testable overload — reads a single <c>vais-ontology://Kind</c> resource.</summary>
    internal static ValueTask<ReadResourceResult> ReadOntologyResourceAsync(string uri, IServiceProvider sp)
    {
        const string prefix = "vais-ontology://";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Unknown resource scheme in '{uri}'. Expected 'vais-ontology://<Kind>'.", nameof(uri));

        var kind = uri[prefix.Length..].Trim('/');
        var catalog = sp.GetRequiredService<IOntologyCatalog>();
        if (!catalog.TryGet(kind, out var entry))
            throw new ArgumentException($"Kind '{kind}' not found in the ontology catalog.", nameof(uri));

        var text = BuildOntologyJson(entry).ToJsonString();
        return new(new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = text }],
        });
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
