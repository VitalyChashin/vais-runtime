// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Vais.Agents.Observability.AgentRunStore;
using Vais.Agents.Observability.GatewayEventStore;
using Vais.Agents.Observability.McpEventStore;
using Vais.Agents.Observability.McpGatewayEventStore;
using Vais.Agents.Observability.RunStore;
using Vais.Agents.Runtime.Extensions;
using Vais.Agents.Runtime.Extensions.Container;
using Vais.Agents.Runtime.Plugins;
using Vais.Agents.Runtime.Plugins.Container;
using Vais.Agents.Runtime.Plugins.Python;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Maps the seven universal <see cref="IAgentLifecycleManager"/> verbs + list /
/// health endpoints to REST routes under a configurable prefix (default
/// <c>/v1</c>). Consumers call <c>app.MapAgentControlPlane()</c> after
/// <c>services.AddAgentControlPlane(...)</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire shape.</b> Request/response bodies are <see cref="AgentManifest"/> and
/// the shipped Abstractions records serialised natively via <see cref="System.Text.Json"/>.
/// All error paths produce RFC 7807 Problem Details via
/// <see cref="ProblemDetailsMapping"/>.
/// </para>
/// </remarks>
public static class AgentControlPlaneEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Mount the control-plane endpoints. Default prefix <c>/v1</c>; the manifest
    /// Create endpoint additionally accepts <c>application/yaml</c> bodies.
    /// </summary>
    public static IEndpointRouteBuilder MapAgentControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = builder.MapGroup(prefix).WithTags("Agents");

        // POST /agents — Create
        group.MapPost("/agents", CreateAsync)
            .WithName("Agents.Create")
            .WithSummary("Create an agent from a manifest.")
            .WithDescription(
                "Accepts an AgentManifest as JSON or YAML (Content-Type: application/json or application/yaml). " +
                "Honours the Idempotency-Key header when the idempotency middleware is mounted — retries with " +
                "the same key + same body replay the original response; different body ⇒ 422 " +
                "urn:vais-agents:idempotency-mismatch.")
            .Accepts<AgentManifest>("application/json", "application/yaml")
            .Produces<AgentApplyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /agents — List (optional label filter, optional cursor / limit)
        group.MapGet("/agents", ListAsync)
            .WithName("Agents.List")
            .WithSummary("List registered agents with optional label-prefix filter.")
            .WithDescription("Query parameters: labels (prefix filter), limit (1..500, default 50).")
            .Produces<AgentListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /agents/{id} — Query (latest version by default, ?version=X for specific)
        group.MapGet("/agents/{id}", QueryAsync)
            .WithName("Agents.Query")
            .WithSummary("Fetch a specific agent manifest + current lifecycle status.")
            .WithDescription("Returns the latest version by default; pass ?version=X to pin.")
            .Produces<AgentQueryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // PATCH /agents/{id} — Update
        group.MapPatch("/agents/{id}", UpdateAsync)
            .WithName("Agents.Update")
            .WithSummary("Publish a new manifest version for an existing agent.")
            .WithDescription(
                "PATCH semantics: supply the full new manifest; the server records it as a new version. " +
                "Honours the Idempotency-Key header when the idempotency middleware is mounted.")
            .Accepts<AgentManifest>("application/json", "application/yaml")
            .Produces<AgentApplyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // DELETE /agents/{id} — Cancel or Evict
        group.MapDelete("/agents/{id}", CancelOrEvictAsync)
            .WithName("Agents.CancelOrEvict")
            .WithSummary("Cancel in-flight work (?mode=cancel) or remove the manifest (?mode=evict).")
            .WithDescription("Default mode is evict. Cancel leaves the manifest registered; Evict removes it.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // POST /agents/{id}/invoke — Invoke (sync)
        group.MapPost("/agents/{id}/invoke", InvokeAsync)
            .WithName("Agents.Invoke")
            .WithSummary("Synchronously invoke an agent; returns the assistant reply.")
            .WithDescription(
                "Body is an AgentInvocationRequest (text + optional metadata). The server routes to the " +
                "target silo/tenant and returns an AgentInvocationResult. Honours Idempotency-Key.")
            .Accepts<AgentInvocationRequest>("application/json")
            .Produces<AgentInvocationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // POST /agents/{id}/invoke/stream — streaming Invoke (SSE)
        group.MapPost("/agents/{id}/invoke/stream", InvokeStreamAsync)
            .WithMetadata(new StreamingEndpointAttribute())
            .WithName("Agents.InvokeStream")
            .WithSummary("Stream an invocation as Server-Sent Events.")
            .WithDescription(
                "Emits the full AgentEvent taxonomy as SSE: turn.started → delta (per text chunk) → " +
                "tool.started / tool.completed (if the model requests tools) → terminal turn.completed " +
                "or turn.failed. SSE event names are stable; body JSON carries the concrete AgentEvent " +
                "record shape. Agents hosted on runtimes that don't implement IStreamingAiAgent return " +
                "501 urn:vais-agents:streaming-not-supported — use POST /v1/agents/{id}/invoke for " +
                "buffered responses. Honours the v0.11 Idempotency-Key middleware opt-out " +
                "(text/event-stream responses bypass the cache).")
            .Accepts<AgentInvocationRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // POST /agents/{id}/signal — Signal
        group.MapPost("/agents/{id}/signal", SignalAsync)
            .WithName("Agents.Signal")
            .WithSummary("Fire-and-forget signal delivery to a running agent.")
            .WithDescription("Body is an AgentSignal (kind + opaque payload). Returns 202 on acceptance.")
            .Accepts<AgentSignal>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /agents/{id}/runs — List invocations for an agent (graph nodes + standalone runs)
        group.MapGet("/agents/{id}/runs", AgentListRunsAsync)
            .WithName("Agents.ListRuns")
            .WithSummary("List invocations for an agent: graph node executions and standalone invoke calls.")
            .WithDescription("Query params: since (ISO 8601), until (ISO 8601), limit (default 20). Returns entries from both AddRunStore() and AddAgentRunStore(); returns 503 only when neither is configured.")
            .Produces<IReadOnlyList<AgentRunDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /agents/{id}/logs — Captured stdout/stderr from agent grains and Python subprocesses
        group.MapGet("/agents/{id}/logs", AgentListLogsAsync)
            .WithName("Agents.ListLogs")
            .WithSummary("Return captured log lines for an agent from the in-memory ring buffer.")
            .WithDescription("Query params: since (ISO 8601), limit (default 100). Requires AddAgentLogSink(); returns 503 when not configured.")
            .Produces<IReadOnlyList<AgentLogEntryDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Health + readiness
        group.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
            .WithName("Agents.Healthz")
            .WithSummary("Liveness probe.")
            .WithTags("Health");
        group.MapGet("/readyz", () => Results.Ok(new { status = "ready" }))
            .WithName("Agents.Readyz")
            .WithSummary("Readiness probe.")
            .WithTags("Health");

        MapGraphControlPlane(builder, prefix);
        MapPluginControlPlane(builder, prefix);
        MapRuntimeTopologyControlPlane(builder, prefix);
        MapLlmGatewayControlPlane(builder, prefix);
        MapMcpGatewayControlPlane(builder, prefix);
        MapMcpServerControlPlane(builder, prefix);
        MapContainerPluginControlPlane(builder, prefix);
        MapExtensionControlPlane(builder, prefix);
        MapEvalSuiteControlPlane(builder, prefix);
        MapEvalRunControlPlane(builder, prefix);
        MapDiagnosticsControlPlane(builder, prefix);

        return builder;
    }

    /// <summary>
    /// Mount only the runtime topology endpoint (v0.34). Useful for hosts that want the
    /// <c>GET /v1/runtimes</c> route without the full control-plane surface, or for isolated testing.
    /// </summary>
    public static IEndpointRouteBuilder MapRuntimeTopologyControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var runtimes = builder.MapGroup(prefix).WithTags("Runtimes");

        // GET /runtimes — Remote runtime topology
        runtimes.MapGet("/runtimes", RuntimeListAsync)
            .WithName("Runtimes.List")
            .WithSummary("List remote runtimes configured on this host.")
            .WithDescription(
                "Returns the URL and identity mode for each remote runtime registered via AddAgentRemoteInvoker. " +
                "Credentials (client secrets, token paths) are never included. " +
                "Returns 200 with an empty items array when no remote runtimes are configured.")
            .Produces<RuntimeListResponse>(StatusCodes.Status200OK);

        return builder;
    }

    /// <summary>
    /// Mount only the plugin inspection endpoint (v0.27). Useful for hosts that want
    /// the plugin list route without the full control-plane surface, or for isolated testing.
    /// </summary>
    public static IEndpointRouteBuilder MapPluginControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var plugins = builder.MapGroup(prefix).WithTags("Plugins");

        // GET /plugins — List
        plugins.MapGet("/plugins", PluginListAsync)
            .WithName("Plugins.List")
            .WithSummary("List all plugins loaded into this host.")
            .WithDescription(
                "Returns a snapshot of every plugin currently loaded by AssemblyPluginLoader or PythonPluginLoader. " +
                "Includes the handler TypeNames each plugin advertises. " +
                "Returns 200 with an empty items array when no plugin infrastructure is registered.")
            .Produces<PluginListResponse>(StatusCodes.Status200OK);

        // POST /plugins/{name}/source — Source push + hot-reload
        plugins.MapPost("/plugins/{name}/source", PushPluginSourceAsync)
            .WithName("Plugins.PushSource")
            .WithSummary("Push a tar.gz source archive and trigger a DrainAndSwap hot-reload.")
            .WithDescription(
                "Accepts a gzip-compressed tar archive (Content-Type: application/gzip) of the plugin source, " +
                "unpacks it over the plugin directory, and calls the DrainAndSwap reloader. " +
                "Returns 503 when hot-reload is disabled (VAIS_PYTHON_PLUGINS_RELOAD_POLICY is not DrainAndSwap).")
            .Accepts<IFormFile>("application/gzip")
            .Produces<PluginSourcePushResponse>(StatusCodes.Status200OK)
            .Produces<PluginSourcePushResponse>(StatusCodes.Status400BadRequest)
            .Produces<PluginSourcePushResponse>(StatusCodes.Status404NotFound)
            .Produces<PluginSourcePushResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<PluginSourcePushResponse>(StatusCodes.Status503ServiceUnavailable);

        // POST /plugins/{name}/image — Container image hot-reload
        plugins.MapPost("/plugins/{name}/image", PushPluginImageAsync)
            .WithName("Plugins.PushImage")
            .WithSummary("Update a container plugin to a new image and trigger a drain/replace hot-reload.")
            .Accepts<PluginImageUpdateRequest>("application/json")
            .Produces<PluginImageUpdateResponse>(StatusCodes.Status200OK)
            .Produces<PluginImageUpdateResponse>(StatusCodes.Status404NotFound)
            .Produces<PluginImageUpdateResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces<PluginImageUpdateResponse>(StatusCodes.Status503ServiceUnavailable);

        // POST /plugins/{name}/dll — C# DLL hot-push + reload
        plugins.MapPost("/plugins/{name}/dll", PushPluginDllAsync)
            .WithName("Plugins.PushDll")
            .WithSummary("Push a compiled C# DLL (or zip) and trigger a DrainAndSwap hot-reload.")
            .WithDescription(
                "Accepts application/octet-stream (raw DLL) or application/zip (DLL + dependencies). " +
                "Pre-validates the [VaisPlugin] ABI attribute without loading the assembly. " +
                "Returns 503 when hot-reload is disabled.")
            .Accepts<IFormFile>("application/octet-stream", "application/zip")
            .Produces<PluginDllPushResponse>(StatusCodes.Status200OK)
            .Produces<PluginDllPushResponse>(StatusCodes.Status201Created)
            .Produces<PluginDllPushResponse>(StatusCodes.Status400BadRequest)
            .Produces<PluginDllPushResponse>(StatusCodes.Status503ServiceUnavailable);

        // POST /plugins — declarative apply: manifest + optional DLL in multipart/form-data
        plugins.MapPost("/plugins", ApplyPluginAsync)
            .WithName("Plugins.Apply")
            .WithSummary("Apply a plugin manifest (multipart: manifest JSON + optional dll).")
            .WithDescription(
                "Accepts multipart/form-data with a 'manifest' field (JSON of PluginManifest) and an " +
                "optional 'dll' file part. For csharp plugins the dll part is required. " +
                "Cross-validates spec.handlers against the DLL's [VaisPlugin] handlers when both are present.")
            .Produces<PluginDllPushResponse>(StatusCodes.Status200OK)
            .Produces<PluginDllPushResponse>(StatusCodes.Status201Created)
            .Produces<PluginDllPushResponse>(StatusCodes.Status400BadRequest)
            .Produces<PluginDllPushResponse>(StatusCodes.Status503ServiceUnavailable)
            .DisableAntiforgery();

        // DELETE /plugins/{name} — unregister and unload a plugin
        plugins.MapDelete("/plugins/{name}", DeletePluginAsync)
            .WithName("Plugins.Delete")
            .WithSummary("Unload and unregister a plugin by name.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // POST /plugins/{name}/import — load a plugin already on disk
        plugins.MapPost("/plugins/{name}/import", ImportExistingPluginAsync)
            .WithName("Plugins.ImportExisting")
            .WithSummary("Load (or hot-reload) a plugin whose DLL is already in the runtime plugins directory.")
            .Produces<PluginDllPushResponse>(StatusCodes.Status200OK)
            .Produces<PluginDllPushResponse>(StatusCodes.Status201Created)
            .Produces<PluginDllPushResponse>(StatusCodes.Status404NotFound)
            .Produces<PluginDllPushResponse>(StatusCodes.Status503ServiceUnavailable);

        return builder;
    }

    /// <summary>
    /// Mount only the graph control-plane endpoints (v0.19). Useful for hosts that
    /// want graph routes without the full agent route surface, or for isolated testing.
    /// </summary>
    public static IEndpointRouteBuilder MapGraphControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var graphs = builder.MapGroup(prefix).WithTags("Graphs");

        // POST /graphs/validate — Validate (dry-run, v0.38)
        graphs.MapPost("/graphs/validate", GraphValidateAsync)
            .WithName("Graphs.Validate")
            .WithSummary("Dry-run validation: structural checks + runtime-context handler resolution. Always 200; inspect Valid/Errors.")
            .Accepts<AgentGraphManifest>("application/json", "application/yaml")
            .Produces<GraphValidationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // POST /graphs — Create
        graphs.MapPost("/graphs", GraphCreateAsync)
            .WithName("Graphs.Create")
            .WithSummary("Register a graph manifest, making it available for invocation.")
            .Accepts<AgentGraphManifest>("application/json", "application/yaml")
            .Produces<AgentGraphApplyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /graphs — List
        graphs.MapGet("/graphs", GraphListAsync)
            .WithName("Graphs.List")
            .WithSummary("List registered graph manifests with optional label-prefix filter.")
            .Produces<AgentGraphListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /graphs/{id} — Query
        graphs.MapGet("/graphs/{id}", GraphQueryAsync)
            .WithName("Graphs.Query")
            .WithSummary("Fetch a graph manifest + current lifecycle status.")
            .Produces<AgentGraphQueryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // PATCH /graphs/{id} — Update
        graphs.MapPatch("/graphs/{id}", GraphUpdateAsync)
            .WithName("Graphs.Update")
            .WithSummary("Publish a new manifest version for an existing graph.")
            .Accepts<AgentGraphManifest>("application/json", "application/yaml")
            .Produces<AgentGraphApplyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // DELETE /graphs/{id} — Evict
        graphs.MapDelete("/graphs/{id}", GraphEvictAsync)
            .WithName("Graphs.Evict")
            .WithSummary("Remove a graph manifest and cancel all in-flight runs.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // POST /graphs/{id}/invoke — Invoke (sync)
        graphs.MapPost("/graphs/{id}/invoke", GraphInvokeAsync)
            .WithName("Graphs.Invoke")
            .WithSummary("Start a new graph run and block until it completes or interrupts.")
            .Accepts<GraphInvocationRequest>("application/json")
            .Produces<GraphInvocationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // POST /graphs/{id}/invoke/stream — Invoke (SSE)
        graphs.MapPost("/graphs/{id}/invoke/stream", GraphInvokeStreamAsync)
            .WithMetadata(new StreamingEndpointAttribute())
            .WithName("Graphs.InvokeStream")
            .WithSummary("Start a graph run and stream events as Server-Sent Events.")
            .Accepts<GraphInvocationRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // POST /graphs/{id}/runs/{runId}/resume — Resume (sync)
        graphs.MapPost("/graphs/{id}/runs/{runId}/resume", GraphResumeAsync)
            .WithName("Graphs.Resume")
            .WithSummary("Resume a previously-interrupted graph run.")
            .Accepts<GraphResumeRequest>("application/json")
            .Produces<GraphInvocationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // POST /graphs/{id}/runs/{runId}/resume/stream — Resume (SSE)
        graphs.MapPost("/graphs/{id}/runs/{runId}/resume/stream", GraphResumeStreamAsync)
            .WithMetadata(new StreamingEndpointAttribute())
            .WithName("Graphs.ResumeStream")
            .WithSummary("Resume a graph run and stream events as Server-Sent Events.")
            .Accepts<GraphResumeRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // DELETE /graphs/{id}/runs/{runId} — Cancel run
        graphs.MapDelete("/graphs/{id}/runs/{runId}", GraphCancelRunAsync)
            .WithName("Graphs.CancelRun")
            .WithSummary("Cancel an in-flight or interrupted graph run.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /graphs/{id}/runs — List historical runs
        graphs.MapGet("/graphs/{id}/runs", GraphListRunsAsync)
            .WithName("Graphs.ListRuns")
            .WithSummary("List historical runs for a graph. Requires AddRunStore().")
            .WithDescription("Query params: status (running|completed|failed|interrupted), since (ISO 8601), until (ISO 8601), limit (default 20). Returns 503 when the run store is not configured.")
            .Produces<RunListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /graphs/{id}/runs/{runId} — Get single run
        graphs.MapGet("/graphs/{id}/runs/{runId}", GraphGetRunAsync)
            .WithName("Graphs.GetRun")
            .WithSummary("Fetch metadata for a single graph run. Requires AddRunStore().")
            .Produces<PipelineRunDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /graphs/{id}/runs/{runId}/nodes — List node executions for a run
        graphs.MapGet("/graphs/{id}/runs/{runId}/nodes", GraphListRunNodesAsync)
            .WithName("Graphs.ListRunNodes")
            .WithSummary("List all node executions for a graph run. Requires AddRunStore().")
            .Produces<IReadOnlyList<NodeExecutionDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /graphs/{id}/runs/{runId}/nodes/{nodeId} — Get single node execution
        graphs.MapGet("/graphs/{id}/runs/{runId}/nodes/{nodeId}", GraphGetRunNodeAsync)
            .WithName("Graphs.GetRunNode")
            .WithSummary("Fetch a single node execution within a graph run. Requires AddRunStore().")
            .Produces<NodeExecutionDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder;
    }

    private static IResult RunStoreNotConfigured() =>
        Results.Problem(
            title: "Run store not configured",
            detail: "AddRunStore() was not called during startup. Historical run data is unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static PipelineRunDto ToRunDto(PipelineRun r) =>
        new(r.RunId, r.GraphId, r.Status.ToString().ToLowerInvariant(),
            r.StartedAt, r.EndedAt, r.DurationMs, r.SuperSteps, r.Error);

    private static NodeExecutionDto ToNodeDto(NodeExecution n) =>
        new(n.RunId, n.NodeId, n.NodeKind, n.AgentId, n.Status.ToString().ToLowerInvariant(),
            n.StartedAt, n.EndedAt, n.DurationMs, n.InputText, n.OutputText,
            n.InputTokens, n.OutputTokens, n.Error, n.EdgesTaken);

    private static RunStatus? ParseRunStatusQuery(string? value) => value?.ToLowerInvariant() switch
    {
        "running" => RunStatus.Running,
        "completed" => RunStatus.Completed,
        "failed" => RunStatus.Failed,
        "interrupted" => RunStatus.Interrupted,
        _ => null,
    };

    private static async Task<IResult> AgentListRunsAsync(
        string id,
        HttpContext http,
        string? since,
        string? until,
        int limit = 20,
        CancellationToken ct = default)
    {
        var runStore = http.RequestServices.GetService<IRunStore>();
        var agentRunStore = http.RequestServices.GetService<IAgentRunStore>();
        if (runStore is null && agentRunStore is null) return RunStoreNotConfigured();

        DateTimeOffset? sinceDto = DateTimeOffset.TryParse(since, out var s) ? s : null;
        DateTimeOffset? untilDto = DateTimeOffset.TryParse(until, out var u) ? u : null;

        var graphItems = runStore is not null
            ? (await runStore.ListNodeExecutionsByAgentAsync(id, sinceDto, untilDto, limit, ct).ConfigureAwait(false))
                .Select(n => ToAgentRunDto(n))
            : [];
        var standaloneItems = agentRunStore is not null
            ? (await agentRunStore.ListRunsAsync(id, sinceDto, untilDto, limit, ct).ConfigureAwait(false))
                .Select(r => ToAgentRunDto(r))
            : [];

        var merged = graphItems.Concat(standaloneItems)
            .OrderByDescending(r => r.StartedAt)
            .Take(limit)
            .ToArray();
        return Results.Ok(merged);
    }

    private static AgentRunDto ToAgentRunDto(NodeExecution n) =>
        new(n.RunId, n.AgentId ?? string.Empty, "graph", n.NodeId, n.NodeKind,
            n.Status.ToString().ToLowerInvariant(),
            n.StartedAt, n.EndedAt, n.DurationMs,
            n.InputText, n.OutputText, n.InputTokens, n.OutputTokens,
            n.Error, n.EdgesTaken);

    private static AgentRunDto ToAgentRunDto(AgentRun r) =>
        new(r.AgentRunId, r.AgentId, "standalone", null, null,
            r.Status.ToString().ToLowerInvariant(),
            r.StartedAt, r.EndedAt, r.DurationMs,
            r.InputText, r.OutputText, r.InputTokens, r.OutputTokens,
            r.Error, null);

    private static IResult AgentListLogsAsync(
        string id,
        HttpContext http,
        string? since,
        int limit = 100)
    {
        var sink = http.RequestServices.GetService<IAgentLogSink>();
        if (sink is null)
            return Results.Problem(
                title: "Agent log sink not configured",
                detail: "Call AddAgentLogSink() to enable agent stdout capture.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "urn:vais-agents:agent-log-sink-not-configured");

        DateTimeOffset? sinceDto = DateTimeOffset.TryParse(since, out var s) ? s : null;
        var limitClamped = Math.Clamp(limit, 1, 1000);

        var items = sink.GetLogs(id, sinceDto, limitClamped);
        return Results.Ok(items.Select(e => new AgentLogEntryDto(
            e.EntryId, e.AgentId, e.RunId, e.At, e.Level, e.Message, e.Source)).ToArray());
    }

    private static async Task<IResult> GraphListRunsAsync(
        string id,
        HttpContext http,
        string? status,
        string? since,
        string? until,
        int limit = 20,
        CancellationToken ct = default)
    {
        var store = http.RequestServices.GetService<IRunStore>();
        if (store is null) return RunStoreNotConfigured();

        DateTimeOffset? sinceDto = DateTimeOffset.TryParse(since, out var s) ? s : null;
        DateTimeOffset? untilDto = DateTimeOffset.TryParse(until, out var u) ? u : null;

        var runs = await store.ListRunsAsync(id, ParseRunStatusQuery(status), sinceDto, untilDto, limit, ct).ConfigureAwait(false);
        return Results.Ok(new RunListResponse(runs.Select(ToRunDto).ToArray()));
    }

    private static async Task<IResult> GraphGetRunAsync(
        string id,
        string runId,
        HttpContext http,
        CancellationToken ct)
    {
        var store = http.RequestServices.GetService<IRunStore>();
        if (store is null) return RunStoreNotConfigured();

        var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null || !string.Equals(run.GraphId, id, StringComparison.Ordinal))
            return Results.NotFound();

        return Results.Ok(ToRunDto(run));
    }

    private static async Task<IResult> GraphListRunNodesAsync(
        string id,
        string runId,
        HttpContext http,
        CancellationToken ct)
    {
        var store = http.RequestServices.GetService<IRunStore>();
        if (store is null) return RunStoreNotConfigured();

        var nodes = await store.GetNodesAsync(runId, ct).ConfigureAwait(false);
        return Results.Ok(nodes.Select(ToNodeDto).ToArray());
    }

    private static async Task<IResult> GraphGetRunNodeAsync(
        string id,
        string runId,
        string nodeId,
        HttpContext http,
        CancellationToken ct)
    {
        var store = http.RequestServices.GetService<IRunStore>();
        if (store is null) return RunStoreNotConfigured();

        var node = await store.GetNodeAsync(runId, nodeId, ct).ConfigureAwait(false);
        if (node is null) return Results.NotFound();

        return Results.Ok(ToNodeDto(node));
    }

    private static IResult RuntimeListAsync(HttpContext http)
    {
        var topology = http.RequestServices.GetService<IRemoteRuntimeTopology>();
        if (topology is null)
            return Results.Ok(new RuntimeListResponse(Array.Empty<RuntimeInfo>()));

        var items = topology.GetEntries()
            .Select(e => new RuntimeInfo(e.Url, e.IdentityMode))
            .ToArray();
        return Results.Ok(new RuntimeListResponse(items));
    }

    private static async Task<IResult> PushPluginSourceAsync(
        string name,
        HttpContext http,
        CancellationToken ct)
    {
        var reloader = http.RequestServices.GetService<IPythonPluginReloader>();
        var host = http.RequestServices.GetService<IPythonPluginHost>();

        if (reloader is null)
            return Results.Json(
                new PluginSourcePushResponse(name, PluginSourcePushStatus.ReloadDisabled, null,
                    "Hot-reload is disabled. Set VAIS_PYTHON_PLUGINS_RELOAD_POLICY=DrainAndSwap to enable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!IsValidPluginName(name))
            return Results.Json(
                new PluginSourcePushResponse(name, PluginSourcePushStatus.UnpackFailed, null,
                    $"Invalid plugin name '{name}'."),
                statusCode: StatusCodes.Status400BadRequest);

        string pluginDirectory;
        var plugin = host?.LoadedPlugins.FirstOrDefault(p =>
            string.Equals(p.Descriptor.Name, name, StringComparison.OrdinalIgnoreCase));

        if (plugin is not null)
        {
            pluginDirectory = plugin.Descriptor.PluginDirectory;
        }
        else if (host is not null)
        {
            // New plugin: derive directory from plugins root (first-push bootstrap path).
            var pluginsRoot = Path.GetFullPath(host.PluginsDirectory);
            pluginDirectory = Path.GetFullPath(Path.Combine(pluginsRoot, name));
            if (!pluginDirectory.StartsWith(pluginsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return Results.Json(
                    new PluginSourcePushResponse(name, PluginSourcePushStatus.UnpackFailed, null,
                        $"Invalid plugin name '{name}'."),
                    statusCode: StatusCodes.Status400BadRequest);
        }
        else
        {
            return Results.Json(
                new PluginSourcePushResponse(name, PluginSourcePushStatus.NoSupervisor, null,
                    $"No plugin '{name}' is loaded and no Python plugin host is configured."),
                statusCode: StatusCodes.Status404NotFound);
        }

        Directory.CreateDirectory(pluginDirectory);

        try
        {
            await UnpackTarGzAsync(http.Request.Body, pluginDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new PluginSourcePushResponse(name, PluginSourcePushStatus.UnpackFailed, null, ex.Message),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await reloader.ReloadAsync(pluginDirectory, ct).ConfigureAwait(false);
        return MapReloadResult(result, host, name);
    }

    private static bool IsValidPluginName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 128 &&
        name.IndexOfAny(['/', '\\', '\0', ':']) == -1 &&
        !name.Equals("..", StringComparison.Ordinal) &&
        !name.Equals(".", StringComparison.Ordinal);

    private static async Task UnpackTarGzAsync(Stream source, string targetDirectory, CancellationToken ct)
    {
        var targetDir = Path.GetFullPath(targetDirectory);
        using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: false);
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory or TarEntryType.SymbolicLink or TarEntryType.HardLink)
                continue;
            var rel = entry.Name.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.GetFullPath(Path.Combine(targetDir, rel));
            if (!dest.StartsWith(targetDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"Path traversal in tar entry '{entry.Name}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (entry.DataStream is { } data)
            {
                await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                await data.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IResult> PushPluginImageAsync(
        string name,
        PluginImageUpdateRequest request,
        HttpContext http,
        CancellationToken ct)
    {
        var reloader = http.RequestServices.GetService<IContainerPluginReloader>();

        if (reloader is null)
            return Results.Json(
                new PluginImageUpdateResponse(name, PluginImageUpdateStatus.NoSupervisor, null),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var result = await reloader.ReloadAsync(name, request.Image, ct).ConfigureAwait(false);
        return MapImageUpdateResult(result);
    }

    private static async Task<IResult> PushPluginDllAsync(
        string name,
        HttpContext http,
        CancellationToken ct)
    {
        var pusher = http.RequestServices.GetService<IAssemblyDllPusher>();

        if (pusher is null)
            return Results.Json(
                new PluginDllPushResponse(name, PluginDllPushStatus.ReloadDisabled, null, null,
                    "Hot-reload is disabled. Set VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap to enable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        if (!IsValidPluginName(name))
            return Results.Json(
                new PluginDllPushResponse(name, PluginDllPushStatus.ValidationFailed, null, null,
                    $"Invalid plugin name '{name}'."),
                statusCode: StatusCodes.Status400BadRequest);

        var contentType = http.Request.ContentType ?? "application/octet-stream";
        var result = await pusher.PushAsync(name, http.Request.Body, contentType, ct).ConfigureAwait(false);

        return result.Status switch
        {
            AssemblyDllPushStatus.Success =>
                Results.Ok(new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.Success,
                    result.Handlers, result.TargetApiVersion, null)),
            AssemblyDllPushStatus.Bootstrapped =>
                Results.Created((string?)null, new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.Bootstrapped,
                    result.Handlers, result.TargetApiVersion, null)),
            AssemblyDllPushStatus.AbiMismatch =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.AbiMismatch, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status400BadRequest),
            AssemblyDllPushStatus.ValidationFailed =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.ValidationFailed, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status400BadRequest),
            AssemblyDllPushStatus.ReloadDisabled =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.ReloadDisabled, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            _ =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.LoadFailed, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status400BadRequest),
        };
    }

    private static async Task<IResult> ApplyPluginAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return Results.Problem("Expected multipart/form-data.", statusCode: StatusCodes.Status400BadRequest);

        var form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);

        var manifestJson = form["manifest"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(manifestJson))
            return Results.Problem("'manifest' form field is required.", statusCode: StatusCodes.Status400BadRequest);

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson, PluginManifestJsonOptions);
        }
        catch
        {
            return Results.Problem("Failed to parse manifest JSON.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            return Results.Problem("manifest.id is required.", statusCode: StatusCodes.Status400BadRequest);

        var language = manifest.Spec?.Language ?? string.Empty;

        if (string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            var pusher = http.RequestServices.GetService<IAssemblyDllPusher>();
            if (pusher is null)
                return Results.Json(
                    new PluginDllPushResponse(manifest.Id, PluginDllPushStatus.ReloadDisabled, null, null,
                        "Hot-reload is disabled. Set VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap to enable."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var dllFile = form.Files.GetFile("dll");
            if (dllFile is null)
                return Results.Json(
                    new PluginDllPushResponse(manifest.Id, PluginDllPushStatus.ValidationFailed, null, null,
                        "The 'dll' form file is required for csharp plugins."),
                    statusCode: StatusCodes.Status400BadRequest);

            var contentType = dllFile.ContentType ?? "application/octet-stream";
            await using var dllStream = dllFile.OpenReadStream();
            var pushResult = await pusher.PushAsync(manifest.Id, dllStream, contentType, ct).ConfigureAwait(false);

            // Cross-validate manifest spec.handlers vs actual DLL handlers on success.
            if (manifest.Spec?.Handlers is { Count: > 0 } expectedHandlers &&
                pushResult.Status is AssemblyDllPushStatus.Success or AssemblyDllPushStatus.Bootstrapped &&
                pushResult.Handlers is not null)
            {
                var actualNames = pushResult.Handlers.ToHashSet(StringComparer.Ordinal);
                var missing = expectedHandlers
                    .Select(h => h.TypeName)
                    .Where(n => !actualNames.Contains(n))
                    .ToList();
                if (missing.Count > 0)
                    return Results.Json(
                        new PluginDllPushResponse(manifest.Id, PluginDllPushStatus.ValidationFailed, null, null,
                            $"Manifest spec.handlers [{string.Join(", ", missing)}] not found in DLL."),
                        statusCode: StatusCodes.Status400BadRequest);
            }

            return MapDllPushResult(pushResult);
        }

        // For non-csharp (python, etc.): validate manifest, no DLL needed.
        return Results.Ok(new PluginDllPushResponse(manifest.Id, PluginDllPushStatus.Success, null, null, null));
    }

    private static async Task<IResult> DeletePluginAsync(string name, HttpContext http, CancellationToken ct)
    {
        var reloader = http.RequestServices.GetService<IPluginReloader>();
        if (reloader is null)
            return Results.Problem("Plugin reloader is not available. Hot-reload may be disabled.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var result = await reloader.UnloadAsync(name, ct).ConfigureAwait(false);

        return result.Status switch
        {
            PluginUnloadStatus.Success => Results.NoContent(),
            PluginUnloadStatus.NotFound => Results.Problem(
                $"Plugin '{name}' is not loaded.",
                statusCode: StatusCodes.Status404NotFound),
            _ => Results.Problem("Failed to unload plugin.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> ImportExistingPluginAsync(string name, HttpContext http, CancellationToken ct)
    {
        var pusher = http.RequestServices.GetService<IAssemblyDllPusher>();
        if (pusher is null)
            return Results.Json(
                new PluginDllPushResponse(name, PluginDllPushStatus.ReloadDisabled, null, null,
                    "Hot-reload is disabled. Set VAIS_PLUGINS_RELOAD_POLICY=DrainAndSwap to enable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var result = await pusher.ImportExistingAsync(name, ct).ConfigureAwait(false);

        return result.Status switch
        {
            AssemblyDllPushStatus.NotFound =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.NotFound, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status404NotFound),
            _ => MapDllPushResult(result),
        };
    }

    private static IResult MapDllPushResult(AssemblyDllPushResult result) =>
        result.Status switch
        {
            AssemblyDllPushStatus.Success =>
                Results.Ok(new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.Success,
                    result.Handlers, result.TargetApiVersion, null)),
            AssemblyDllPushStatus.Bootstrapped =>
                Results.Created((string?)null, new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.Bootstrapped,
                    result.Handlers, result.TargetApiVersion, null)),
            AssemblyDllPushStatus.AbiMismatch =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.AbiMismatch, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status400BadRequest),
            AssemblyDllPushStatus.ValidationFailed =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.ValidationFailed, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status400BadRequest),
            AssemblyDllPushStatus.ReloadDisabled =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.ReloadDisabled, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status503ServiceUnavailable),
            _ =>
                Results.Json(
                    new PluginDllPushResponse(result.PluginName, PluginDllPushStatus.LoadFailed, null, null, result.ErrorMessage),
                    statusCode: StatusCodes.Status400BadRequest),
        };

    private static readonly JsonSerializerOptions PluginManifestJsonOptions = new(JsonSerializerDefaults.Web);

    private static IResult MapImageUpdateResult(ContainerPluginReloadResult result) =>
        result.Status switch
        {
            ContainerPluginReloadStatus.Success =>
                Results.Ok(new PluginImageUpdateResponse(result.PluginName, PluginImageUpdateStatus.Success, null)),
            ContainerPluginReloadStatus.HandlerTypeNameChanged =>
                Results.Json(
                    new PluginImageUpdateResponse(result.PluginName, PluginImageUpdateStatus.HandlerTypeNameChanged, result.FailureUrn),
                    statusCode: StatusCodes.Status422UnprocessableEntity),
            ContainerPluginReloadStatus.NoSupervisor =>
                Results.Json(
                    new PluginImageUpdateResponse(result.PluginName, PluginImageUpdateStatus.NoSupervisor, result.FailureUrn),
                    statusCode: StatusCodes.Status404NotFound),
            ContainerPluginReloadStatus.HandshakeFailed =>
                Results.Ok(new PluginImageUpdateResponse(result.PluginName, PluginImageUpdateStatus.HandshakeFailed, result.FailureUrn)),
            ContainerPluginReloadStatus.StartFailed =>
                Results.Ok(new PluginImageUpdateResponse(result.PluginName, PluginImageUpdateStatus.StartFailed, result.FailureUrn)),
            ContainerPluginReloadStatus.RolloutStarted =>
                Results.Accepted(value: new PluginImageUpdateResponse(result.PluginName, PluginImageUpdateStatus.RolloutStarted, null)),
            _ =>
                Results.Ok(new PluginImageUpdateResponse(result.PluginName, PluginImageUpdateStatus.HandshakeFailed, result.FailureUrn)),
        };

    private static IResult MapReloadResult(PythonPluginReloadResult result, IPythonPluginHost? host, string pluginName)
    {
        var currentPid = host?.LoadedPlugins
            .FirstOrDefault(p => string.Equals(p.Descriptor.Name, result.PluginName, StringComparison.OrdinalIgnoreCase))
            ?.ProcessId;
        return result.Status switch
        {
            PythonPluginReloadStatus.Success =>
                Results.Ok(new PluginSourcePushResponse(result.PluginName, PluginSourcePushStatus.Success, currentPid, null)),
            PythonPluginReloadStatus.Bootstrapped =>
                Results.Created((string?)null,
                    new PluginSourcePushResponse(result.PluginName, PluginSourcePushStatus.Bootstrapped, currentPid, null)),
            PythonPluginReloadStatus.HandshakeFailed =>
                Results.Ok(new PluginSourcePushResponse(result.PluginName, PluginSourcePushStatus.HandshakeFailed, null, result.FailureUrn)),
            PythonPluginReloadStatus.HandlerTypeNameChanged =>
                Results.Json(
                    new PluginSourcePushResponse(result.PluginName, PluginSourcePushStatus.HandlerTypeNameChanged, null, result.FailureUrn),
                    statusCode: StatusCodes.Status422UnprocessableEntity),
            PythonPluginReloadStatus.ScanFailed =>
                Results.Json(
                    new PluginSourcePushResponse(result.PluginName, PluginSourcePushStatus.ScanFailed, null, result.FailureUrn),
                    statusCode: StatusCodes.Status400BadRequest),
            PythonPluginReloadStatus.BootstrapFailed =>
                Results.Json(
                    new PluginSourcePushResponse(result.PluginName, PluginSourcePushStatus.BootstrapFailed, null, result.FailureUrn),
                    statusCode: StatusCodes.Status500InternalServerError),
            _ =>
                Results.Json(
                    new PluginSourcePushResponse(result.PluginName, PluginSourcePushStatus.NoSupervisor, null, result.FailureUrn),
                    statusCode: StatusCodes.Status404NotFound),
        };
    }

    private static IResult PluginListAsync(HttpContext http)
    {
        var items = new List<PluginInfo>();

        var registry = http.RequestServices.GetService<IPluginHandlerRegistry>();
        if (registry is not null)
        {
            foreach (var d in registry.Plugins)
            {
                items.Add(new PluginInfo(d.Name, d.AssemblyPath, d.TargetApiVersion, d.Handlers, d.LoadedViaAttribute)
                {
                    Kind = PluginKind.Assembly,
                    State = PluginState.Ready,
                });
            }
        }

        var pythonHost = http.RequestServices.GetService<IPythonPluginHost>();
        if (pythonHost is not null)
        {
            foreach (var p in pythonHost.LoadedPlugins)
            {
                items.Add(new PluginInfo(
                    Name: p.Descriptor.Name,
                    AssemblyPath: string.Empty,
                    TargetApiVersion: p.Descriptor.TargetApiVersion,
                    Handlers: p.Descriptor.HandlerTypeName is not null
                        ? [p.Descriptor.HandlerTypeName]
                        : [],
                    LoadedViaAttribute: false)
                {
                    Kind = PluginKind.Python,
                    State = p.Status switch
                    {
                        PythonPluginStatus.Loading => PluginState.Loading,
                        PythonPluginStatus.Ready => PluginState.Ready,
                        PythonPluginStatus.Restarting => PluginState.Restarting,
                        _ => PluginState.Unavailable,
                    },
                    ProcessId = p.ProcessId,
                    ToolNames = p.Descriptor.DeclaredTools.Count > 0
                        ? p.Descriptor.DeclaredTools
                        : null,
                    LastErrorSnippet = p.LastErrorSnippet,
                });
            }
        }

        var containerHost = http.RequestServices.GetService<IContainerPluginHost>();
        if (containerHost is not null)
        {
            foreach (var p in containerHost.LoadedPlugins)
            {
                items.Add(new PluginInfo(
                    Name: p.Name,
                    AssemblyPath: string.Empty,
                    TargetApiVersion: p.TargetApiVersion,
                    Handlers: string.IsNullOrEmpty(p.HandlerTypeName) ? [] : [p.HandlerTypeName],
                    LoadedViaAttribute: false)
                {
                    Kind = PluginKind.Container,
                    State = p.Status switch
                    {
                        ContainerPluginStatus.Ready    => PluginState.Ready,
                        ContainerPluginStatus.Starting => PluginState.Loading,
                        ContainerPluginStatus.Created  => PluginState.Loading,
                        _                              => PluginState.Unavailable,
                    },
                    Image = p.Image,
                    Topology = p.Topology,
                    KubernetesDeploymentName = p.KubernetesDeploymentName,
                    KubernetesNamespace = p.KubernetesNamespace,
                });
            }
        }

        return Results.Ok(new PluginListResponse(items));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        IAgentLifecycleManager manager,
        IAgentManifestLoader loader,
        CapturingManifestApplyDiagnosticsSink? sink,
        CancellationToken ct)
    {
        string body;
        using (var reader = new StreamReader(http.Request.Body))
        {
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        IReadOnlyList<AgentManifest> parsed;
        try
        {
            parsed = await loader.LoadFromStringAsync(body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, instance: http.Request.Path, operation: PolicyOperation.Create);
        }

        if (parsed.Count != 1)
        {
            return Results.BadRequest(new { error = $"POST /agents accepts exactly one manifest; got {parsed.Count}." });
        }

        var manifest = parsed[0];
        try
        {
            // GCF-18: eager ref validation — resolve gateway refs before persisting
            var refError = await ValidateAgentGatewayRefsAsync(http, manifest, ct).ConfigureAwait(false);
            if (refError is not null) return refError;

            using var capture = sink?.BeginCapture();
            var handle = await manager.CreateAsync(manifest, ct).ConfigureAwait(false);
            var warnings = capture?.Drain() ?? Array.Empty<ApplyDiagnostic>();
            return Results.Created(
                $"{http.Request.PathBase}{http.Request.Path}/{manifest.Id}",
                new AgentApplyResponse(handle, warnings));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, manifest.Id, PolicyOperation.Create);
        }
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        IAgentRegistry registry,
        string? labels,
        int? limit,
        CancellationToken ct)
    {
        try
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var items = new List<AgentManifest>();
            var prefix = labels; // Pass-through to ListAsync's labelPrefix — implementations may refine.
            await foreach (var m in registry.ListAsync(prefix, ct).ConfigureAwait(false))
            {
                items.Add(m);
                if (items.Count >= take) break;
            }
            return Results.Ok(new AgentListResponse(items, NextCursor: null));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.Query);
        }
    }

    private static async Task<IResult> QueryAsync(
        HttpContext http,
        IAgentRegistry registry,
        IAgentLifecycleManager manager,
        string id,
        string? version,
        CancellationToken ct)
    {
        try
        {
            var manifest = await registry.GetAsync(id, version, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                return Results.NotFound(new { error = $"agent '{id}' not found" });
            }
            var handle = new AgentHandle(id, manifest.Version);
            var status = await manager.QueryAsync(handle, ct).ConfigureAwait(false);
            return Results.Ok(new AgentQueryResponse(manifest, handle, status));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Query);
        }
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext http,
        IAgentLifecycleManager manager,
        IAgentRegistry registry,
        IAgentManifestLoader loader,
        CapturingManifestApplyDiagnosticsSink? sink,
        string id,
        string? version,
        CancellationToken ct)
    {
        string body;
        using (var reader = new StreamReader(http.Request.Body))
        {
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        IReadOnlyList<AgentManifest> parsed;
        try
        {
            parsed = await loader.LoadFromStringAsync(body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Update);
        }

        if (parsed.Count != 1)
        {
            return Results.BadRequest(new { error = $"PATCH /agents/{{id}} accepts exactly one manifest; got {parsed.Count}." });
        }

        var manifest = parsed[0];
        try
        {
            var existingVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (existingVersion is null)
            {
                return Results.NotFound(new { error = $"agent '{id}' not found" });
            }
            var currentHandle = new AgentHandle(id, existingVersion);
            using var capture = sink?.BeginCapture();
            var newHandle = await manager.UpdateAsync(currentHandle, manifest, ct).ConfigureAwait(false);
            var warnings = capture?.Drain() ?? Array.Empty<ApplyDiagnostic>();
            return Results.Ok(new AgentApplyResponse(newHandle, warnings));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Update);
        }
    }

    private static async Task<IResult> CancelOrEvictAsync(
        HttpContext http,
        IAgentLifecycleManager manager,
        IAgentRegistry registry,
        string id,
        string? version,
        string? mode,
        CancellationToken ct)
    {
        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
            {
                return Results.NotFound(new { error = $"agent '{id}' not found" });
            }
            var handle = new AgentHandle(id, resolvedVersion);
            var op = string.Equals(mode, "cancel", StringComparison.OrdinalIgnoreCase)
                ? PolicyOperation.Cancel
                : PolicyOperation.Evict; // default = evict per the schema doc
            if (op == PolicyOperation.Cancel)
            {
                await manager.CancelAsync(handle, ct).ConfigureAwait(false);
            }
            else
            {
                await manager.EvictAsync(handle, ct).ConfigureAwait(false);
            }
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id);
        }
    }

    private static async Task<IResult> InvokeAsync(
        HttpContext http,
        IAgentLifecycleManager manager,
        IAgentRegistry registry,
        string id,
        string? version,
        CancellationToken ct)
    {
        AgentInvocationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<AgentInvocationRequest>(ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Invoke);
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.BadRequest(new { error = "request body must contain non-empty 'text'" });
        }

        var agentRunStore = http.RequestServices.GetService<IAgentRunStore>();
        var agentRunId = Guid.NewGuid().ToString("N");
        var principal = (http.User?.Identity?.IsAuthenticated == true
            ? new AgentContext(
                UserId: http.User.FindFirst("sub")?.Value,
                TenantId: http.User.FindFirst("tenant_id")?.Value ?? http.User.FindFirst("tid")?.Value)
            : AgentContext.Empty)
            with { CorrelationId = ResolveCorrelationId(http) };
        using var _ = http.RequestServices.GetService<IAgentContextSetter>()?.Push(principal);
        var runStarted = false;

        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
            {
                return Results.NotFound(new { error = $"agent '{id}' not found" });
            }
            var handle = new AgentHandle(id, resolvedVersion);

            if (agentRunStore is not null)
            {
                await agentRunStore.StartRunAsync(agentRunId, id, TruncateText(request.Text),
                    principal.UserId, principal.TenantId, principal.CorrelationId, ct).ConfigureAwait(false);
                runStarted = true;
            }

            var result = await manager.InvokeAsync(handle, request, ct).ConfigureAwait(false);

            if (runStarted)
                try { await agentRunStore!.CompleteRunAsync(agentRunId, TruncateText(result.Text), 0, 0, ct).ConfigureAwait(false); }
                catch { /* best-effort */ }

            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            if (runStarted)
                try { await agentRunStore!.FailRunAsync(agentRunId, ex.Message, ct).ConfigureAwait(false); }
                catch { /* best-effort */ }
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Invoke);
        }
    }

    private static string ResolveCorrelationId(HttpContext http) =>
        http.Request.Headers.TryGetValue("X-Correlation-Id", out var cid) && cid.Count > 0
            ? cid.ToString()
            : Guid.NewGuid().ToString("N");

    private static string ResolveWorkspace(HttpContext http) =>
        http.Request.Headers.TryGetValue("X-Vais-Workspace", out var ws) && ws.Count > 0
            ? ws.ToString()
            : "default";

    private static string? TruncateText(string? text, int maxChars = 8192) =>
        text is null ? null :
        text.Length <= maxChars ? text :
        string.Concat(text.AsSpan(0, maxChars), "…");

    private static async Task<IResult> SignalAsync(
        HttpContext http,
        IAgentLifecycleManager manager,
        IAgentRegistry registry,
        string id,
        string? version,
        CancellationToken ct)
    {
        AgentSignal? signal;
        try
        {
            signal = await http.Request.ReadFromJsonAsync<AgentSignal>(ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Signal);
        }
        if (signal is null || string.IsNullOrWhiteSpace(signal.Kind))
        {
            return Results.BadRequest(new { error = "signal body must contain non-empty 'kind'" });
        }

        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
            {
                return Results.NotFound(new { error = $"agent '{id}' not found" });
            }
            var handle = new AgentHandle(id, resolvedVersion);
            await manager.SignalAsync(handle, signal, ct).ConfigureAwait(false);
            return Results.Accepted();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Signal);
        }
    }

    private static async Task InvokeStreamAsync(
        HttpContext http,
        IAgentRegistry registry,
        IAgentRuntime runtime,
        string id,
        string? version,
        CancellationToken ct)
    {
        // Read the body first so early-exit Problem Details paths can write JSON + set 4xx status
        // before the SSE handshake commits the response.
        AgentInvocationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<AgentInvocationRequest>(ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteProblemAsync(http, ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Invoke)).ConfigureAwait(false);
            return;
        }
        if (request is null || string.IsNullOrWhiteSpace(request.Text))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "request body must contain non-empty 'text'" }, ct).ConfigureAwait(false);
            return;
        }

        // Resolve agent (no lifecycle-manager middleware for v0.12 — streaming bypasses
        // policy + audit by design; documented limitation).
        var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
        if (resolvedVersion is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = $"agent '{id}' not found" }, ct).ConfigureAwait(false);
            return;
        }

        IAiAgent agent;
        try
        {
            agent = runtime.GetOrCreate(id);
        }
        catch (Exception ex)
        {
            await WriteProblemAsync(http, ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Invoke)).ConfigureAwait(false);
            return;
        }

        if (agent is not IStreamingAiAgent streamable)
        {
            await WriteProblemAsync(http, ProblemDetailsMapping.StreamingNotSupported(id, http.Request.Path)).ConfigureAwait(false);
            return;
        }

        // SSE handshake — set content-type FIRST so the v0.11 idempotency middleware's
        // post-next(ctx) content-type check sees text/event-stream and opts out of caching.
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers["Cache-Control"] = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx response buffering
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);

        // Channel-multiplex design: agent-event loop writes event frames; heartbeat timer
        // writes ':' comment lines. Single SSE-writer task drains to the response body.
        // Linked CTS coordinates shutdown on client abort.
        var heartbeat = http.RequestServices.GetService<IOptions<StreamingInvokeOptions>>()?.Value.HeartbeatInterval
                        ?? TimeSpan.FromSeconds(15);
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writerCt = linkedCts.Token;

        Timer? heartbeatTimer = null;
        if (heartbeat > TimeSpan.Zero)
        {
            heartbeatTimer = new Timer(
                _ => channel.Writer.TryWrite($": heartbeat {DateTimeOffset.UtcNow:O}\n\n"),
                state: null,
                dueTime: heartbeat,
                period: heartbeat);
        }

        // Agent producer task — drives StreamEventsCore, serialises each event to SSE,
        // writes to the channel. Terminates on completion or on cancellation.
        var principal = (http.User?.Identity?.IsAuthenticated == true
            ? new AgentContext(
                UserId: http.User.FindFirst("sub")?.Value,
                TenantId: http.User.FindFirst("tenant_id")?.Value ?? http.User.FindFirst("tid")?.Value)
            : AgentContext.Empty)
            with { CorrelationId = ResolveCorrelationId(http) };
        using var _ctxScope = http.RequestServices.GetService<IAgentContextSetter>()?.Push(principal);

        var agentRunStore = http.RequestServices.GetService<IAgentRunStore>();
        var agentRunId = Guid.NewGuid().ToString("N");
        var runStarted = false;
        if (agentRunStore is not null)
        {
            try
            {
                await agentRunStore.StartRunAsync(agentRunId, id, TruncateText(request.Text),
                    principal.UserId, principal.TenantId, principal.CorrelationId, ct).ConfigureAwait(false);
                runStarted = true;
            }
            catch { /* best-effort */ }
        }

        Exception? producerError = null;
        var producerTask = Task.Run(async () =>
        {
            var terminalSeen = false;
            try
            {
                await foreach (var evt in streamable.StreamAsync(request.Text, principal, writerCt).ConfigureAwait(false))
                {
                    if (evt is TurnCompleted or TurnFailed)
                        terminalSeen = true;
                    var (eventName, dataJson) = AgentEventSerializer.Serialize(evt);
                    var frame = $"event: {eventName}\ndata: {dataJson}\n\n";
                    await channel.Writer.WriteAsync(frame, writerCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* cooperative shutdown */ }
            catch (Exception ex)
            {
                producerError = ex;
                // StatefulAiAgent.StreamAsync yields a TurnFailed and then re-throws (P9), so when a
                // terminal event was already streamed, that yielded event is the terminal — don't emit
                // a second one (it would bypass the errorInterceptor rewrite and break the single-
                // TurnFailed contract). Synthesize only when the producer threw without a terminal.
                if (!terminalSeen)
                {
                    var turnFailed = new TurnFailed(DateTimeOffset.UtcNow, principal, ex.GetType().Name, ex.Message, TimeSpan.Zero);
                    var (eventName, dataJson) = AgentEventSerializer.Serialize(turnFailed);
                    var frame = $"event: {eventName}\ndata: {dataJson}\n\n";
                    try { await channel.Writer.WriteAsync(frame, writerCt).ConfigureAwait(false); }
                    catch { /* swallow — writer already gone */ }
                }
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, writerCt);

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(writerCt).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(frame);
                await http.Response.Body.WriteAsync(bytes, writerCt).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(writerCt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* client aborted — producer task shuts down via linked CTS */ }
        finally
        {
            linkedCts.Cancel();
            heartbeatTimer?.Dispose();
            try { await producerTask.ConfigureAwait(false); } catch { /* already surfaced via frame or cancelled */ }
            if (runStarted)
            {
                if (producerError is not null)
                    try { await agentRunStore!.FailRunAsync(agentRunId, producerError.Message, CancellationToken.None).ConfigureAwait(false); } catch { }
                else
                    try { await agentRunStore!.CompleteRunAsync(agentRunId, null, 0, 0, CancellationToken.None).ConfigureAwait(false); } catch { }
            }
        }
    }

    private static async Task WriteProblemAsync(HttpContext http, IResult result)
    {
        await result.ExecuteAsync(http).ConfigureAwait(false);
    }

    // ── Graph handlers (v0.19) ─────────────────────────────────────────────

    private static async Task<IResult> GraphValidateAsync(
        HttpContext http,
        CancellationToken ct)
    {
        var graphLoader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
        {
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        IReadOnlyList<AgentGraphManifest> parsed;
        try
        {
            parsed = await graphLoader.LoadFromStringAsync(body, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.GraphCreate);
        }
        catch (AgentManifestValidationException ex)
        {
            return Results.Ok(new GraphValidationResult(Valid: false, ex.Errors.ToArray()));
        }

        if (parsed.Count != 1)
        {
            return Results.BadRequest(new { error = $"POST /graphs/validate accepts exactly one AgentGraph manifest; got {parsed.Count}." });
        }

        var manifest = parsed[0];
        var errors = new List<string>();

        var pluginRegistry = http.RequestServices.GetService<IPluginHandlerRegistry>();
        var agentRegistry = http.RequestServices.GetService<IAgentRegistry>();

        foreach (var node in manifest.Nodes)
        {
            if (string.Equals(node.Kind, "Code", StringComparison.Ordinal) && node.HandlerRef is not null)
            {
                if (pluginRegistry is not null &&
                    !pluginRegistry.HandlerTypeNames.Contains(node.HandlerRef.TypeName))
                {
                    errors.Add($"Code-kind node '{node.Id}': handler '{node.HandlerRef.TypeName}' is not registered in any loaded plugin");
                }
            }
            else if (string.Equals(node.Kind, "Agent", StringComparison.Ordinal) && node.Ref is not null)
            {
                if (agentRegistry is not null)
                {
                    var found = await agentRegistry.GetAsync(node.Ref.Id, node.Ref.Version, ct).ConfigureAwait(false);
                    if (found is null)
                    {
                        var versionHint = node.Ref.Version is null ? string.Empty : $" v{node.Ref.Version}";
                        errors.Add($"Agent-kind node '{node.Id}': agent '{node.Ref.Id}'{versionHint} is not registered on this runtime");
                    }
                }
            }
        }

        return Results.Ok(new GraphValidationResult(Valid: errors.Count == 0, errors));
    }

    private static async Task<IResult> GraphCreateAsync(
        HttpContext http,
        CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        var graphLoader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
        {
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        AgentGraphManifest manifest;
        try
        {
            var graphs = await graphLoader.LoadFromStringAsync(body, ct).ConfigureAwait(false);
            if (graphs.Count != 1)
            {
                return Results.BadRequest(new { error = $"POST /graphs accepts exactly one AgentGraph manifest; got {graphs.Count}." });
            }
            manifest = graphs[0];
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.GraphCreate);
        }

        try
        {
            var agentRegistry = http.RequestServices.GetService<IAgentRegistry>();
            var warnings = await RunRegistryOutputSchemaCheckAsync(manifest, agentRegistry, ct).ConfigureAwait(false);
            var handle = await manager.CreateAsync(manifest, ct).ConfigureAwait(false);
            return Results.Created($"{http.Request.PathBase}{http.Request.Path}/{manifest.Id}", new AgentGraphApplyResponse(handle, warnings));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, manifest.Id, PolicyOperation.GraphCreate);
        }
    }

    private static async Task<IResult> GraphListAsync(
        HttpContext http,
        string? labels,
        int? limit,
        CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        try
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var items = new List<AgentGraphManifest>();
            await foreach (var m in registry.ListAsync(labels, ct).ConfigureAwait(false))
            {
                items.Add(m);
                if (items.Count >= take) break;
            }
            return Results.Ok(new AgentGraphListResponse(items, NextCursor: null));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.GraphQuery);
        }
    }

    private static async Task<IResult> GraphQueryAsync(
        HttpContext http,
        string id,
        string? version,
        CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        try
        {
            var manifest = await registry.GetAsync(id, version, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                return Results.NotFound(new { error = $"graph '{id}' not found" });
            }
            var handle = new AgentGraphHandle(id, manifest.Version);
            var status = await manager.QueryAsync(handle, ct).ConfigureAwait(false);
            return Results.Ok(new AgentGraphQueryResponse(manifest, handle, status));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphQuery);
        }
    }

    private static async Task<IResult> GraphUpdateAsync(
        HttpContext http,
        string id,
        string? version,
        CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        var graphLoader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
        {
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        AgentGraphManifest newManifest;
        try
        {
            var graphs = await graphLoader.LoadFromStringAsync(body, ct).ConfigureAwait(false);
            if (graphs.Count != 1)
            {
                return Results.BadRequest(new { error = $"PATCH /graphs/{{id}} accepts exactly one AgentGraph manifest; got {graphs.Count}." });
            }
            newManifest = graphs[0];
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphUpdate);
        }

        try
        {
            var existingVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (existingVersion is null)
            {
                return Results.NotFound(new { error = $"graph '{id}' not found" });
            }
            var agentRegistry = http.RequestServices.GetService<IAgentRegistry>();
            var warnings = await RunRegistryOutputSchemaCheckAsync(newManifest, agentRegistry, ct).ConfigureAwait(false);
            var currentHandle = new AgentGraphHandle(id, existingVersion);
            var newHandle = await manager.UpdateAsync(currentHandle, newManifest, ct).ConfigureAwait(false);
            return Results.Ok(new AgentGraphApplyResponse(newHandle, warnings));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphUpdate);
        }
    }

    private static async Task<IReadOnlyList<ApplyDiagnostic>> RunRegistryOutputSchemaCheckAsync(
        AgentGraphManifest manifest,
        IAgentRegistry? agentRegistry,
        CancellationToken ct)
    {
        if (agentRegistry is null) return Array.Empty<ApplyDiagnostic>();
        var agentRefs = manifest.Nodes
            .Where(n => string.Equals(n.Kind, "Agent", StringComparison.Ordinal)
                     && n.Ref is not null
                     && n.Ref.RuntimeUrl is null
                     && n.Ref.A2AUrl is null
                     && n.StateBindings?.Output is { Count: > 0 })
            .Select(n => (n.Ref!.Id, n.Ref.Version))
            .Distinct()
            .ToList();
        if (agentRefs.Count == 0) return Array.Empty<ApplyDiagnostic>();
        var resolved = new Dictionary<(string, string?), AgentManifest?>(agentRefs.Count);
        foreach (var (agentId, agentVersion) in agentRefs)
        {
            resolved[(agentId, agentVersion)] = await agentRegistry.GetAsync(agentId, agentVersion, ct).ConfigureAwait(false);
        }
        var errors = new List<string>();
        AgentGraphManifestValidator.ValidateAgentOutputSchemaBindings(
            manifest,
            (id, version) => resolved.TryGetValue((id, version), out var m) ? m : null,
            errors);
        return errors.Count == 0
            ? Array.Empty<ApplyDiagnostic>()
            : errors.Select(e => new ApplyDiagnostic("urn:vais-agents:graph:output-schema-mismatch", e)).ToArray();
    }

    private static async Task<IResult> GraphEvictAsync(
        HttpContext http,
        string id,
        string? version,
        CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
            {
                return Results.NotFound(new { error = $"graph '{id}' not found" });
            }
            await manager.EvictAsync(new AgentGraphHandle(id, resolvedVersion), ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id);
        }
    }

    private static async Task<IResult> GraphInvokeAsync(
        HttpContext http,
        string id,
        string? version,
        CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        GraphInvocationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<GraphInvocationRequest>(ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphInvoke);
        }
        if (request is null)
        {
            return Results.BadRequest(new { error = "request body is required" });
        }

        var graphPrincipal = (http.User?.Identity?.IsAuthenticated == true
            ? new AgentContext(
                UserId: http.User.FindFirst("sub")?.Value,
                TenantId: http.User.FindFirst("tenant_id")?.Value ?? http.User.FindFirst("tid")?.Value)
            : AgentContext.Empty)
            with { CorrelationId = ResolveCorrelationId(http) };
        using var _gCtx = http.RequestServices.GetService<IAgentContextSetter>()?.Push(graphPrincipal);

        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
            {
                return Results.NotFound(new { error = $"graph '{id}' not found" });
            }
            var handle = new AgentGraphHandle(id, resolvedVersion);
            var result = await manager.InvokeAsync(handle, request, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphInvoke);
        }
    }

    private static async Task GraphInvokeStreamAsync(
        HttpContext http,
        string id,
        string? version,
        CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        GraphInvocationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<GraphInvocationRequest>(ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            await WriteProblemAsync(http, ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphInvoke)).ConfigureAwait(false);
            return;
        }
        if (request is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "request body is required" }, ct).ConfigureAwait(false);
            return;
        }

        var graphStreamPrincipal = (http.User?.Identity?.IsAuthenticated == true
            ? new AgentContext(
                UserId: http.User.FindFirst("sub")?.Value,
                TenantId: http.User.FindFirst("tenant_id")?.Value ?? http.User.FindFirst("tid")?.Value)
            : AgentContext.Empty)
            with { CorrelationId = ResolveCorrelationId(http) };
        using var _gsCtx = http.RequestServices.GetService<IAgentContextSetter>()?.Push(graphStreamPrincipal);

        var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
        if (resolvedVersion is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = $"graph '{id}' not found" }, ct).ConfigureAwait(false);
            return;
        }

        var handle = new AgentGraphHandle(id, resolvedVersion);
        await StreamGraphEventsAsync(http, manager.InvokeStreamAsync(handle, request, ct), ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GraphResumeAsync(
        HttpContext http,
        string id,
        string runId,
        string? version,
        CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        GraphResumeRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<GraphResumeRequest>(ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphResume);
        }
        if (request is null)
        {
            return Results.BadRequest(new { error = "resume request body is required" });
        }

        // Enforce that the path runId matches the body — path is canonical; body is optional convenience.
        var effectiveRequest = string.IsNullOrEmpty(request.RunId)
            ? request with { RunId = runId }
            : request;

        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
            {
                return Results.NotFound(new { error = $"graph '{id}' not found" });
            }
            var handle = new AgentGraphHandle(id, resolvedVersion);
            var result = await manager.ResumeAsync(handle, effectiveRequest, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphResume);
        }
    }

    private static async Task GraphResumeStreamAsync(
        HttpContext http,
        string id,
        string runId,
        string? version,
        CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        GraphResumeRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<GraphResumeRequest>(ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            await WriteProblemAsync(http, ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphResume)).ConfigureAwait(false);
            return;
        }
        if (request is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "resume request body is required" }, ct).ConfigureAwait(false);
            return;
        }

        var effectiveRequest = string.IsNullOrEmpty(request.RunId) ? request with { RunId = runId } : request;

        var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
        if (resolvedVersion is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = $"graph '{id}' not found" }, ct).ConfigureAwait(false);
            return;
        }

        var handle = new AgentGraphHandle(id, resolvedVersion);
        await StreamGraphEventsAsync(http, manager.ResumeStreamAsync(handle, effectiveRequest, ct), ct).ConfigureAwait(false);
    }

    private static async Task<IResult> GraphCancelRunAsync(
        HttpContext http,
        string id,
        string runId,
        string? version,
        CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IAgentGraphLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IAgentGraphRegistry>();
        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
            {
                return Results.NotFound(new { error = $"graph '{id}' not found" });
            }
            await manager.CancelAsync(new AgentGraphHandle(id, resolvedVersion), runId, ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id);
        }
    }

    private static async Task StreamGraphEventsAsync(
        HttpContext http,
        IAsyncEnumerable<AgentGraphEvent> events,
        CancellationToken ct)
    {
        var heartbeat = http.RequestServices.GetService<IOptions<StreamingInvokeOptions>>()?.Value.HeartbeatInterval
                        ?? TimeSpan.FromSeconds(15);
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writerCt = linkedCts.Token;

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers["Cache-Control"] = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);

        Timer? heartbeatTimer = null;
        if (heartbeat > TimeSpan.Zero)
        {
            heartbeatTimer = new Timer(
                _ => channel.Writer.TryWrite($": heartbeat {DateTimeOffset.UtcNow:O}\n\n"),
                state: null, dueTime: heartbeat, period: heartbeat);
        }

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in events.ConfigureAwait(false))
                {
                    var (eventName, dataJson) = AgentGraphEventSerializer.Serialize(evt);
                    await channel.Writer.WriteAsync($"event: {eventName}\ndata: {dataJson}\n\n", writerCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                var failed = new GraphFailed(DateTimeOffset.UtcNow, AgentContext.Empty, RunId: "?", SuperStep: 0,
                    ErrorType: ex.GetType().Name, ErrorMessage: ex.Message, Duration: TimeSpan.Zero);
                var (name, json) = AgentGraphEventSerializer.Serialize(failed);
                try { await channel.Writer.WriteAsync($"event: {name}\ndata: {json}\n\n", writerCt).ConfigureAwait(false); } catch { }
            }
            finally { channel.Writer.TryComplete(); }
        }, writerCt);

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(writerCt).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(frame);
                await http.Response.Body.WriteAsync(bytes, writerCt).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(writerCt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            linkedCts.Cancel();
            heartbeatTimer?.Dispose();
            try { await producerTask.ConfigureAwait(false); } catch { }
        }
    }

    // ── LLM gateway config handlers (v0.20) ───────────────────────────────────

    /// <summary>
    /// Mount only the LLM gateway config control-plane endpoints (v0.20). Useful for
    /// hosts that want these routes without the full agent route surface, or for isolated testing.
    /// </summary>
    public static IEndpointRouteBuilder MapLlmGatewayControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = builder.MapGroup(prefix).WithTags("LlmGateways");

        group.MapPost("/llm-gateways/validate", LlmGatewayValidateAsync)
            .WithName("LlmGateways.Validate")
            .WithSummary("Dry-run validation: structural checks. Always 200; inspect Valid/Errors.")
            .Accepts<LlmGatewayConfigManifest>("application/json", "application/yaml")
            .Produces<LlmGatewayConfigValidationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/llm-gateways", LlmGatewayCreateAsync)
            .WithName("LlmGateways.Create")
            .WithSummary("Register an LLM gateway config manifest.")
            .Accepts<LlmGatewayConfigManifest>("application/json", "application/yaml")
            .Produces<LlmGatewayConfigApplyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/llm-gateways", LlmGatewayListAsync)
            .WithName("LlmGateways.List")
            .WithSummary("List registered LLM gateway config manifests with optional label-prefix filter.")
            .Produces<LlmGatewayConfigListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/llm-gateways/{id}", LlmGatewayQueryAsync)
            .WithName("LlmGateways.Query")
            .WithSummary("Fetch an LLM gateway config manifest + current lifecycle status.")
            .Produces<LlmGatewayConfigQueryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPatch("/llm-gateways/{id}", LlmGatewayUpdateAsync)
            .WithName("LlmGateways.Update")
            .WithSummary("Publish a new manifest version for an existing LLM gateway config.")
            .Accepts<LlmGatewayConfigManifest>("application/json", "application/yaml")
            .Produces<LlmGatewayConfigApplyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/llm-gateways/{id}", LlmGatewayEvictAsync)
            .WithName("LlmGateways.Evict")
            .WithSummary("Remove an LLM gateway config manifest.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/llm-gateways/{id}/events", LlmGatewayListEventsAsync)
            .WithName("LlmGateways.ListEvents")
            .WithSummary("List LLM completion events for a gateway. Requires AddGatewayEventStore().")
            .WithDescription("Query params: since (ISO 8601), until (ISO 8601), kind (completion.completed | completion.failed), limit (default 50). Returns 503 when store not configured.")
            .Produces<IReadOnlyList<GatewayEventDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder;
    }

    private static async Task<IResult> LlmGatewayValidateAsync(HttpContext http, CancellationToken ct)
    {
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var configs = resources.OfType<ManifestResource.LlmGatewayConfigCase>().ToList();
            if (configs.Count != 1)
                return Results.BadRequest(new { error = $"POST /llm-gateways/validate accepts exactly one LlmGatewayConfig manifest; got {configs.Count}." });
            return Results.Ok(new LlmGatewayConfigValidationResult(Valid: true, Errors: Array.Empty<string>()));
        }
        catch (AgentManifestValidationException ex)
        {
            return Results.Ok(new LlmGatewayConfigValidationResult(Valid: false, ex.Errors.ToArray()));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.LlmGatewayConfigCreate);
        }
    }

    private static async Task<IResult> LlmGatewayCreateAsync(HttpContext http, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<ILlmGatewayConfigLifecycleManager>();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        LlmGatewayConfigManifest manifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var configs = resources.OfType<ManifestResource.LlmGatewayConfigCase>().ToList();
            if (configs.Count != 1)
                return Results.BadRequest(new { error = $"POST /llm-gateways accepts exactly one LlmGatewayConfig manifest; got {configs.Count}." });
            manifest = configs[0].Config;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.LlmGatewayConfigCreate);
        }

        try
        {
            var handle = await manager.CreateAsync(manifest, ct).ConfigureAwait(false);
            return Results.Created($"{http.Request.PathBase}{http.Request.Path}/{manifest.Id}",
                new LlmGatewayConfigApplyResponse(handle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, manifest.Id, PolicyOperation.LlmGatewayConfigCreate);
        }
    }

    private static async Task<IResult> LlmGatewayListAsync(HttpContext http, string? labels, int? limit, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<ILlmGatewayConfigRegistry>();
        try
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var items = new List<LlmGatewayConfigManifest>();
            await foreach (var m in registry.ListAsync(labels, ct).ConfigureAwait(false))
            {
                items.Add(m);
                if (items.Count >= take) break;
            }
            return Results.Ok(new LlmGatewayConfigListResponse(items, NextCursor: null));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.LlmGatewayConfigQuery);
        }
    }

    private static async Task<IResult> LlmGatewayQueryAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<ILlmGatewayConfigRegistry>();
        var manager = http.RequestServices.GetRequiredService<ILlmGatewayConfigLifecycleManager>();
        try
        {
            var manifest = await registry.GetAsync(id, version, ct).ConfigureAwait(false);
            if (manifest is null)
                return Results.NotFound(new { error = $"llm-gateway '{id}' not found" });
            var handle = new LlmGatewayConfigHandle(id, manifest.Version);
            var status = await manager.QueryAsync(handle, ct).ConfigureAwait(false);
            return Results.Ok(new LlmGatewayConfigQueryResponse(manifest, handle, status));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.LlmGatewayConfigQuery);
        }
    }

    private static async Task<IResult> LlmGatewayUpdateAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<ILlmGatewayConfigLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<ILlmGatewayConfigRegistry>();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        LlmGatewayConfigManifest newManifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var configs = resources.OfType<ManifestResource.LlmGatewayConfigCase>().ToList();
            if (configs.Count != 1)
                return Results.BadRequest(new { error = $"PATCH /llm-gateways/{{id}} accepts exactly one LlmGatewayConfig manifest; got {configs.Count}." });
            newManifest = configs[0].Config;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.LlmGatewayConfigUpdate);
        }

        try
        {
            var existingVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (existingVersion is null)
                return Results.NotFound(new { error = $"llm-gateway '{id}' not found" });
            var currentHandle = new LlmGatewayConfigHandle(id, existingVersion);
            var newHandle = await manager.UpdateAsync(currentHandle, newManifest, ct).ConfigureAwait(false);
            return Results.Ok(new LlmGatewayConfigApplyResponse(newHandle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.LlmGatewayConfigUpdate);
        }
    }

    private static async Task<IResult> LlmGatewayEvictAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<ILlmGatewayConfigLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<ILlmGatewayConfigRegistry>();
        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
                return Results.NotFound(new { error = $"llm-gateway '{id}' not found" });
            await manager.EvictAsync(new LlmGatewayConfigHandle(id, resolvedVersion), ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id);
        }
    }

    private static async Task<IResult> LlmGatewayListEventsAsync(
        HttpContext http,
        string id,
        string? since,
        string? until,
        string? kind,
        int limit = 50,
        CancellationToken ct = default)
    {
        var store = http.RequestServices.GetService<IGatewayEventStore>();
        if (store is null)
            return Results.Problem(
                title: "Gateway event store not configured",
                detail: "Call AddGatewayEventStore() to enable LLM gateway event history.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "urn:vais-agents:gateway-event-store-not-configured");

        DateTimeOffset? sinceDto = DateTimeOffset.TryParse(since, out var s) ? s : null;
        DateTimeOffset? untilDto = DateTimeOffset.TryParse(until, out var u) ? u : null;
        var limitClamped = Math.Clamp(limit, 1, 500);

        var items = await store.ListAsync(id, sinceDto, untilDto, kind, limitClamped, ct)
            .ConfigureAwait(false);
        return Results.Ok(items.Select(e => new GatewayEventDto(
            e.EventId, e.GatewayId, e.EventKind, e.ModelId,
            e.InputTokens, e.OutputTokens, e.DurationMs, e.CacheHit,
            e.ErrorType, e.At, e.CorrelationId, e.RunId,
            e.InputJson, e.OutputJson)).ToArray());
    }

    // ── MCP gateway config handlers (v0.20) ───────────────────────────────────

    /// <summary>
    /// Mount only the MCP gateway config control-plane endpoints (v0.20).
    /// </summary>
    public static IEndpointRouteBuilder MapMcpGatewayControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = builder.MapGroup(prefix).WithTags("McpGateways");

        group.MapPost("/mcp-gateways/validate", McpGatewayValidateAsync)
            .WithName("McpGateways.Validate")
            .WithSummary("Dry-run validation: structural checks. Always 200; inspect Valid/Errors.")
            .Accepts<McpGatewayConfigManifest>("application/json", "application/yaml")
            .Produces<McpGatewayConfigValidationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/mcp-gateways", McpGatewayCreateAsync)
            .WithName("McpGateways.Create")
            .WithSummary("Register an MCP gateway config manifest.")
            .Accepts<McpGatewayConfigManifest>("application/json", "application/yaml")
            .Produces<McpGatewayConfigApplyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/mcp-gateways", McpGatewayListAsync)
            .WithName("McpGateways.List")
            .WithSummary("List registered MCP gateway config manifests with optional label-prefix filter.")
            .Produces<McpGatewayConfigListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/mcp-gateways/{id}", McpGatewayQueryAsync)
            .WithName("McpGateways.Query")
            .WithSummary("Fetch an MCP gateway config manifest + current lifecycle status.")
            .Produces<McpGatewayConfigQueryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPatch("/mcp-gateways/{id}", McpGatewayUpdateAsync)
            .WithName("McpGateways.Update")
            .WithSummary("Publish a new manifest version for an existing MCP gateway config.")
            .Accepts<McpGatewayConfigManifest>("application/json", "application/yaml")
            .Produces<McpGatewayConfigApplyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/mcp-gateways/{id}", McpGatewayEvictAsync)
            .WithName("McpGateways.Evict")
            .WithSummary("Remove an MCP gateway config manifest.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/mcp-gateways/{id}/events", McpGatewayListEventsAsync)
            .WithName("McpGateways.ListEvents")
            .WithSummary("List MCP tool-call events for a gateway. Requires AddMcpGatewayEventStore().")
            .WithDescription("Query params: since (ISO 8601), until (ISO 8601), toolName, kind (call.completed | call.failed | call.blocked | cache.hit), limit (default 50). Returns 503 when store not configured.")
            .Produces<McpGatewayEventDto[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder;
    }

    private static async Task<IResult> McpGatewayValidateAsync(HttpContext http, CancellationToken ct)
    {
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var configs = resources.OfType<ManifestResource.McpGatewayConfigCase>().ToList();
            if (configs.Count != 1)
                return Results.BadRequest(new { error = $"POST /mcp-gateways/validate accepts exactly one McpGatewayConfig manifest; got {configs.Count}." });
            return Results.Ok(new McpGatewayConfigValidationResult(Valid: true, Errors: Array.Empty<string>()));
        }
        catch (AgentManifestValidationException ex)
        {
            return Results.Ok(new McpGatewayConfigValidationResult(Valid: false, ex.Errors.ToArray()));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.McpGatewayConfigCreate);
        }
    }

    private static async Task<IResult> McpGatewayCreateAsync(HttpContext http, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IMcpGatewayConfigLifecycleManager>();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        McpGatewayConfigManifest manifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var configs = resources.OfType<ManifestResource.McpGatewayConfigCase>().ToList();
            if (configs.Count != 1)
                return Results.BadRequest(new { error = $"POST /mcp-gateways accepts exactly one McpGatewayConfig manifest; got {configs.Count}." });
            manifest = configs[0].Config;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.McpGatewayConfigCreate);
        }

        try
        {
            var handle = await manager.CreateAsync(manifest, ct).ConfigureAwait(false);
            return Results.Created($"{http.Request.PathBase}{http.Request.Path}/{manifest.Id}",
                new McpGatewayConfigApplyResponse(handle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, manifest.Id, PolicyOperation.McpGatewayConfigCreate);
        }
    }

    private static async Task<IResult> McpGatewayListAsync(HttpContext http, string? labels, int? limit, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IMcpGatewayConfigRegistry>();
        try
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var items = new List<McpGatewayConfigManifest>();
            await foreach (var m in registry.ListAsync(labels, ct).ConfigureAwait(false))
            {
                items.Add(m);
                if (items.Count >= take) break;
            }
            return Results.Ok(new McpGatewayConfigListResponse(items, NextCursor: null));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.McpGatewayConfigQuery);
        }
    }

    private static async Task<IResult> McpGatewayQueryAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IMcpGatewayConfigRegistry>();
        var manager = http.RequestServices.GetRequiredService<IMcpGatewayConfigLifecycleManager>();
        try
        {
            var manifest = await registry.GetAsync(id, version, ct).ConfigureAwait(false);
            if (manifest is null)
                return Results.NotFound(new { error = $"mcp-gateway '{id}' not found" });
            var handle = new McpGatewayConfigHandle(id, manifest.Version);
            var status = await manager.QueryAsync(handle, ct).ConfigureAwait(false);
            return Results.Ok(new McpGatewayConfigQueryResponse(manifest, handle, status));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.McpGatewayConfigQuery);
        }
    }

    private static async Task<IResult> McpGatewayUpdateAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IMcpGatewayConfigLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IMcpGatewayConfigRegistry>();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        McpGatewayConfigManifest newManifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var configs = resources.OfType<ManifestResource.McpGatewayConfigCase>().ToList();
            if (configs.Count != 1)
                return Results.BadRequest(new { error = $"PATCH /mcp-gateways/{{id}} accepts exactly one McpGatewayConfig manifest; got {configs.Count}." });
            newManifest = configs[0].Config;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.McpGatewayConfigUpdate);
        }

        try
        {
            var existingVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (existingVersion is null)
                return Results.NotFound(new { error = $"mcp-gateway '{id}' not found" });
            var currentHandle = new McpGatewayConfigHandle(id, existingVersion);
            var newHandle = await manager.UpdateAsync(currentHandle, newManifest, ct).ConfigureAwait(false);
            return Results.Ok(new McpGatewayConfigApplyResponse(newHandle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.McpGatewayConfigUpdate);
        }
    }

    private static async Task<IResult> McpGatewayEvictAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IMcpGatewayConfigLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IMcpGatewayConfigRegistry>();
        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
                return Results.NotFound(new { error = $"mcp-gateway '{id}' not found" });
            await manager.EvictAsync(new McpGatewayConfigHandle(id, resolvedVersion), ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id);
        }
    }

    private static async Task<IResult> McpGatewayListEventsAsync(
        HttpContext http,
        string id,
        string? since,
        string? until,
        string? toolName,
        string? kind,
        int limit = 50,
        CancellationToken ct = default)
    {
        var store = http.RequestServices.GetService<IMcpGatewayEventStore>();
        if (store is null)
            return Results.Problem(
                title: "MCP gateway event store not configured",
                detail: "Call AddMcpGatewayEventStore() to enable MCP gateway tool-call event history.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "urn:vais-agents:mcp-gateway-event-store-not-configured");

        DateTimeOffset? sinceDto = DateTimeOffset.TryParse(since, out var s) ? s : null;
        DateTimeOffset? untilDto = DateTimeOffset.TryParse(until, out var u) ? u : null;
        var limitClamped = Math.Clamp(limit, 1, 500);

        var items = await store.ListAsync(id, sinceDto, untilDto, toolName, kind, limitClamped, ct)
            .ConfigureAwait(false);
        return Results.Ok(items.Select(e => new McpGatewayEventDto(
            e.EventId, e.GatewayId, e.ToolName, e.EventKind, e.DurationMs,
            e.CacheHit, e.BlockedReason, e.ErrorType, e.At, e.CorrelationId, e.RunId,
            e.InputJson, e.OutputJson)).ToArray());
    }

    // ── MCP server handlers (v0.20) ───────────────────────────────────────────

    /// <summary>
    /// Mount only the MCP server control-plane endpoints (v0.20).
    /// </summary>
    public static IEndpointRouteBuilder MapMcpServerControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = builder.MapGroup(prefix).WithTags("McpServers");

        group.MapPost("/mcp-servers/validate", McpServerValidateAsync)
            .WithName("McpServers.Validate")
            .WithSummary("Dry-run validation: structural checks + source-ref resolution. Always 200; inspect Valid/Errors.")
            .Accepts<McpServerManifest>("application/json", "application/yaml")
            .Produces<McpServerValidationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/mcp-servers", McpServerCreateAsync)
            .WithName("McpServers.Create")
            .WithSummary("Register an MCP server manifest.")
            .Accepts<McpServerManifest>("application/json", "application/yaml")
            .Produces<McpServerApplyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/mcp-servers", McpServerListAsync)
            .WithName("McpServers.List")
            .WithSummary("List registered MCP server manifests with optional label-prefix filter.")
            .Produces<McpServerListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/mcp-servers/{id}", McpServerQueryAsync)
            .WithName("McpServers.Query")
            .WithSummary("Fetch an MCP server manifest + current lifecycle status.")
            .Produces<McpServerQueryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPatch("/mcp-servers/{id}", McpServerUpdateAsync)
            .WithName("McpServers.Update")
            .WithSummary("Publish a new manifest version for an existing MCP server.")
            .Accepts<McpServerManifest>("application/json", "application/yaml")
            .Produces<McpServerApplyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/mcp-servers/{id}", McpServerEvictAsync)
            .WithName("McpServers.Evict")
            .WithSummary("Remove an MCP server manifest.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/mcp-servers/{id}/events", McpListEventsAsync)
            .WithName("McpServers.ListEvents")
            .WithSummary("List MCP tool-call events for a server. Requires AddMcpEventStore().")
            .WithDescription("Query params: since (ISO 8601), until (ISO 8601), toolName, kind (call.completed | call.failed | call.blocked | cache.hit), limit (default 50). Returns 503 when store not configured.")
            .Produces<McpEventDto[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder;
    }

    private static async Task<IResult> McpServerValidateAsync(HttpContext http, CancellationToken ct)
    {
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        IReadOnlyList<ManifestResource> resources;
        try
        {
            resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
        }
        catch (AgentManifestValidationException ex)
        {
            return Results.Ok(new McpServerValidationResult(Valid: false, ex.Errors.ToArray()));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.McpServerCreate);
        }

        var servers = resources.OfType<ManifestResource.McpServerCase>().ToList();
        if (servers.Count != 1)
            return Results.BadRequest(new { error = $"POST /mcp-servers/validate accepts exactly one McpServer manifest; got {servers.Count}." });

        var manifest = servers[0].Server;
        var errors = await ValidateMcpServerRefsAsync(http, manifest, ct).ConfigureAwait(false);
        return Results.Ok(new McpServerValidationResult(Valid: errors.Count == 0, errors));
    }

    private static async Task<IResult> McpServerCreateAsync(HttpContext http, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IMcpServerLifecycleManager>();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        McpServerManifest manifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var servers = resources.OfType<ManifestResource.McpServerCase>().ToList();
            if (servers.Count != 1)
                return Results.BadRequest(new { error = $"POST /mcp-servers accepts exactly one McpServer manifest; got {servers.Count}." });
            manifest = servers[0].Server;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.McpServerCreate);
        }

        try
        {
            var handle = await manager.CreateAsync(manifest, ct).ConfigureAwait(false);
            return Results.Created($"{http.Request.PathBase}{http.Request.Path}/{manifest.Id}",
                new McpServerApplyResponse(handle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, manifest.Id, PolicyOperation.McpServerCreate);
        }
    }

    private static async Task<IResult> McpServerListAsync(HttpContext http, string? labels, int? limit, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IMcpServerRegistry>();
        try
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var items = new List<McpServerManifest>();
            await foreach (var m in registry.ListAsync(labels, ct).ConfigureAwait(false))
            {
                items.Add(m);
                if (items.Count >= take) break;
            }
            return Results.Ok(new McpServerListResponse(items, NextCursor: null));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.McpServerQuery);
        }
    }

    private static async Task<IResult> McpServerQueryAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IMcpServerRegistry>();
        var manager = http.RequestServices.GetRequiredService<IMcpServerLifecycleManager>();
        try
        {
            var manifest = await registry.GetAsync(id, version, ct).ConfigureAwait(false);
            if (manifest is null)
                return Results.NotFound(new { error = $"mcp-server '{id}' not found" });
            var handle = new McpServerHandle(id, manifest.Version);
            var status = await manager.QueryAsync(handle, ct).ConfigureAwait(false);
            return Results.Ok(new McpServerQueryResponse(manifest, handle, status));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.McpServerQuery);
        }
    }

    private static async Task<IResult> McpServerUpdateAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IMcpServerLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IMcpServerRegistry>();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        McpServerManifest newManifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var servers = resources.OfType<ManifestResource.McpServerCase>().ToList();
            if (servers.Count != 1)
                return Results.BadRequest(new { error = $"PATCH /mcp-servers/{{id}} accepts exactly one McpServer manifest; got {servers.Count}." });
            newManifest = servers[0].Server;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.McpServerUpdate);
        }

        try
        {
            var existingVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (existingVersion is null)
                return Results.NotFound(new { error = $"mcp-server '{id}' not found" });
            var currentHandle = new McpServerHandle(id, existingVersion);
            var newHandle = await manager.UpdateAsync(currentHandle, newManifest, ct).ConfigureAwait(false);
            return Results.Ok(new McpServerApplyResponse(newHandle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.McpServerUpdate);
        }
    }

    private static async Task<IResult> McpServerEvictAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var manager = http.RequestServices.GetRequiredService<IMcpServerLifecycleManager>();
        var registry = http.RequestServices.GetRequiredService<IMcpServerRegistry>();
        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
                return Results.NotFound(new { error = $"mcp-server '{id}' not found" });
            await manager.EvictAsync(new McpServerHandle(id, resolvedVersion), ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id);
        }
    }

    private static async Task<IResult> McpListEventsAsync(
        HttpContext http,
        string id,
        string? since,
        string? until,
        string? toolName,
        string? kind,
        int limit = 50,
        CancellationToken ct = default)
    {
        var store = http.RequestServices.GetService<IMcpEventStore>();
        if (store is null)
            return Results.Problem(
                title: "MCP event store not configured",
                detail: "Call AddMcpEventStore() to enable MCP tool-call event history.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "urn:vais-agents:mcp-event-store-not-configured");

        DateTimeOffset? sinceDto = DateTimeOffset.TryParse(since, out var s) ? s : null;
        DateTimeOffset? untilDto = DateTimeOffset.TryParse(until, out var u) ? u : null;
        var limitClamped = Math.Clamp(limit, 1, 500);

        var items = await store.ListAsync(id, sinceDto, untilDto, toolName, kind, limitClamped, ct)
            .ConfigureAwait(false);
        return Results.Ok(items.Select(e => new McpEventDto(
            e.EventId, e.ServerId, e.ToolName, e.EventKind, e.DurationMs,
            e.CacheHit, e.BlockedReason, e.ErrorType, e.At, e.CorrelationId, e.RunId,
            e.InputJson, e.OutputJson)).ToArray());
    }

    // ── GCF-18: gateway ref validation helpers ────────────────────────────────

    private static async Task<IResult?> ValidateAgentGatewayRefsAsync(HttpContext http, AgentManifest manifest, CancellationToken ct)
    {
        if (manifest.LlmGatewayRef is { } llmRef)
        {
            var llmRegistry = http.RequestServices.GetService<ILlmGatewayConfigRegistry>();
            if (llmRegistry is not null)
            {
                var found = await llmRegistry.GetAsync(llmRef, version: null, ct).ConfigureAwait(false);
                if (found is null)
                    return Results.UnprocessableEntity(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Type = ProblemDetailsMapping.TypePrefix + "ref-not-found",
                        Title = "LlmGatewayRef not found",
                        Status = StatusCodes.Status422UnprocessableEntity,
                        Detail = $"LlmGatewayConfig '{llmRef}' is not registered. Apply it before the agent.",
                    });
            }
        }

        if (manifest.McpGatewayRef is { } mcpRef)
        {
            var mcpRegistry = http.RequestServices.GetService<IMcpGatewayConfigRegistry>();
            if (mcpRegistry is not null)
            {
                var found = await mcpRegistry.GetAsync(mcpRef, version: null, ct).ConfigureAwait(false);
                if (found is null)
                    return Results.UnprocessableEntity(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Type = ProblemDetailsMapping.TypePrefix + "ref-not-found",
                        Title = "McpGatewayRef not found",
                        Status = StatusCodes.Status422UnprocessableEntity,
                        Detail = $"McpGatewayConfig '{mcpRef}' is not registered. Apply it before the agent.",
                    });
            }
        }

        if (manifest.McpServers is { Count: > 0 } mcpServers)
        {
            var serverRegistry = http.RequestServices.GetService<IMcpServerRegistry>();
            if (serverRegistry is not null)
            {
                foreach (var serverRef in mcpServers)
                {
                    if (!string.Equals(serverRef.Transport, McpServerRef.RegisteredTransport, StringComparison.Ordinal))
                        continue;
                    if (string.IsNullOrEmpty(serverRef.Name)) continue;
                    var found = await serverRegistry.GetAsync(serverRef.Name, version: null, ct).ConfigureAwait(false);
                    if (found is null)
                        return Results.UnprocessableEntity(new Microsoft.AspNetCore.Mvc.ProblemDetails
                        {
                            Type = ProblemDetailsMapping.TypePrefix + "ref-not-found",
                            Title = "McpServerRef not found",
                            Status = StatusCodes.Status422UnprocessableEntity,
                            Detail = $"McpServer '{serverRef.Name}' is not registered. Apply it before the agent.",
                        });
                }
            }
        }

        return null;
    }

    private static async Task<List<string>> ValidateMcpServerRefsAsync(HttpContext http, McpServerManifest manifest, CancellationToken ct)
    {
        var errors = new List<string>();

        if (manifest.Virtual && manifest.Sources is { Count: > 0 } sources)
        {
            var serverRegistry = http.RequestServices.GetService<IMcpServerRegistry>();
            if (serverRegistry is not null)
            {
                foreach (var src in sources)
                {
                    var found = await serverRegistry.GetAsync(src.Ref, version: null, ct).ConfigureAwait(false);
                    if (found is null)
                        errors.Add($"Sources[*].Ref '{src.Ref}': no registered McpServer with that id");
                }
            }
        }

        if (manifest.McpGatewayRef is { } gwRef)
        {
            var mcpRegistry = http.RequestServices.GetService<IMcpGatewayConfigRegistry>();
            if (mcpRegistry is not null)
            {
                var found = await mcpRegistry.GetAsync(gwRef, version: null, ct).ConfigureAwait(false);
                if (found is null)
                    errors.Add($"McpGatewayRef '{gwRef}': no registered McpGatewayConfig with that id");
            }
        }

        return errors;
    }

    // ── Container plugin handlers (v0.21) ─────────────────────────────────────

    /// <summary>
    /// Mount only the container plugin control-plane endpoints (v0.21).
    /// </summary>
    public static IEndpointRouteBuilder MapContainerPluginControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = builder.MapGroup(prefix).WithTags("ContainerPlugins");

        group.MapPost("/container-plugins/validate", ContainerPluginValidateAsync)
            .WithName("ContainerPlugins.Validate")
            .WithSummary("Dry-run validation: structural checks only. Always 200; inspect Valid/Errors.")
            .Accepts<ContainerPluginManifest>("application/json", "application/yaml")
            .Produces<ContainerPluginValidationResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/container-plugins", ContainerPluginCreateAsync)
            .WithName("ContainerPlugins.Create")
            .WithSummary("Register a container plugin manifest and start the container.")
            .Accepts<ContainerPluginManifest>("application/json", "application/yaml")
            .Produces<ContainerPluginApplyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/container-plugins", ContainerPluginListAsync)
            .WithName("ContainerPlugins.List")
            .WithSummary("List registered container plugin manifests with optional label-prefix filter.")
            .Produces<ContainerPluginListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/container-plugins/{id}", ContainerPluginQueryAsync)
            .WithName("ContainerPlugins.Query")
            .WithSummary("Fetch a container plugin manifest + current runtime status.")
            .Produces<ContainerPluginQueryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPatch("/container-plugins/{id}", ContainerPluginUpdateAsync)
            .WithName("ContainerPlugins.Update")
            .WithSummary("Publish a new manifest version for an existing container plugin.")
            .Accepts<ContainerPluginManifest>("application/json", "application/yaml")
            .Produces<ContainerPluginApplyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/container-plugins/{id}", ContainerPluginEvictAsync)
            .WithName("ContainerPlugins.Evict")
            .WithSummary("Stop and remove a container plugin.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder;
    }

    private static async Task<IResult> ContainerPluginValidateAsync(HttpContext http, CancellationToken ct)
    {
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        IReadOnlyList<ManifestResource> resources;
        try
        {
            resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
        }
        catch (AgentManifestValidationException ex)
        {
            return Results.Ok(new ContainerPluginValidationResult(Valid: false, ex.Errors.ToArray()));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.ContainerPluginCreate);
        }

        var plugins = resources.OfType<ManifestResource.ContainerPluginCase>().ToList();
        if (plugins.Count != 1)
            return Results.BadRequest(new { error = $"POST /container-plugins/validate accepts exactly one ContainerPlugin manifest; got {plugins.Count}." });

        return Results.Ok(new ContainerPluginValidationResult(Valid: true, Array.Empty<string>()));
    }

    private static IResult ContainerPluginsNotAvailable() =>
        Results.Problem(
            title: "Container plugin support not enabled",
            detail: "Set VAIS_CONTAINER_PLUGINS_DIRECTORY in the runtime environment to enable container plugins.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<IResult> ContainerPluginCreateAsync(HttpContext http, CancellationToken ct)
    {
        var manager = http.RequestServices.GetService<IContainerPluginLifecycleManager>();
        if (manager is null) return ContainerPluginsNotAvailable();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        ContainerPluginManifest manifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var plugins = resources.OfType<ManifestResource.ContainerPluginCase>().ToList();
            if (plugins.Count != 1)
                return Results.BadRequest(new { error = $"POST /container-plugins accepts exactly one ContainerPlugin manifest; got {plugins.Count}." });
            manifest = plugins[0].Manifest;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.ContainerPluginCreate);
        }

        try
        {
            var handle = await manager.CreateAsync(manifest, ct).ConfigureAwait(false);
            return Results.Created($"{http.Request.PathBase}{http.Request.Path}/{manifest.Id}",
                new ContainerPluginApplyResponse(handle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, manifest.Id, PolicyOperation.ContainerPluginCreate);
        }
    }

    private static async Task<IResult> ContainerPluginListAsync(HttpContext http, string? labels, int? limit, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IContainerPluginRegistry>();
        try
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var items = new List<ContainerPluginManifest>();
            await foreach (var m in registry.ListAsync(labels, ct).ConfigureAwait(false))
            {
                items.Add(m);
                if (items.Count >= take) break;
            }
            return Results.Ok(new ContainerPluginListResponse(items, NextCursor: null));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.ContainerPluginQuery);
        }
    }

    private static async Task<IResult> ContainerPluginQueryAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IContainerPluginRegistry>();
        var manager = http.RequestServices.GetService<IContainerPluginLifecycleManager>();
        if (manager is null) return ContainerPluginsNotAvailable();
        try
        {
            var manifest = await registry.GetAsync(id, version, ct).ConfigureAwait(false);
            if (manifest is null)
                return Results.NotFound(new { error = $"container-plugin '{id}' not found" });
            var handle = new ContainerPluginHandle(id, manifest.Version);
            var status = await manager.QueryAsync(handle, ct).ConfigureAwait(false);
            return Results.Ok(new ContainerPluginQueryResponse(manifest, handle, status));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.ContainerPluginQuery);
        }
    }

    private static async Task<IResult> ContainerPluginUpdateAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var manager = http.RequestServices.GetService<IContainerPluginLifecycleManager>();
        if (manager is null) return ContainerPluginsNotAvailable();
        var registry = http.RequestServices.GetRequiredService<IContainerPluginRegistry>();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        ContainerPluginManifest newManifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var plugins = resources.OfType<ManifestResource.ContainerPluginCase>().ToList();
            if (plugins.Count != 1)
                return Results.BadRequest(new { error = $"PATCH /container-plugins/{{id}} accepts exactly one ContainerPlugin manifest; got {plugins.Count}." });
            newManifest = plugins[0].Manifest;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.ContainerPluginUpdate);
        }

        try
        {
            var existingVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (existingVersion is null)
                return Results.NotFound(new { error = $"container-plugin '{id}' not found" });
            var currentHandle = new ContainerPluginHandle(id, existingVersion);
            var newHandle = await manager.UpdateAsync(currentHandle, newManifest, ct).ConfigureAwait(false);
            return Results.Ok(new ContainerPluginApplyResponse(newHandle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.ContainerPluginUpdate);
        }
    }

    private static async Task<IResult> ContainerPluginEvictAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var manager = http.RequestServices.GetService<IContainerPluginLifecycleManager>();
        if (manager is null) return ContainerPluginsNotAvailable();
        var registry = http.RequestServices.GetRequiredService<IContainerPluginRegistry>();
        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
                return Results.NotFound(new { error = $"container-plugin '{id}' not found" });
            await manager.EvictAsync(new ContainerPluginHandle(id, resolvedVersion), ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id);
        }
    }

    // ── Extension endpoints (EXT-11) ─────────────────────────────────────────

    /// <summary>
    /// Mount the extension control-plane endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapExtensionControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var extensions = builder.MapGroup(prefix).WithTags("Extensions");

        // POST /extensions — apply (create or update) a C# extension via multipart/form-data
        extensions.MapPost("/extensions", ApplyExtensionAsync)
            .WithName("Extensions.Apply")
            .WithSummary("Apply an extension manifest + optional DLL (multipart/form-data).")
            .WithDescription(
                "Accepts multipart/form-data with a 'manifest' YAML file part and an optional 'dll' file part. " +
                "For host:csharp extensions the dll part is required. " +
                "Returns 200 on update, 201 on first-time load, 400 on ABI mismatch, 409 on priority conflict, 422 on load failure.")
            .Produces<ExtensionApplyResponse>(StatusCodes.Status200OK)
            .Produces<ExtensionApplyResponse>(StatusCodes.Status201Created)
            .Produces<ExtensionApplyResponse>(StatusCodes.Status400BadRequest)
            .Produces<ExtensionApplyResponse>(StatusCodes.Status409Conflict)
            .Produces<ExtensionApplyResponse>(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        // DELETE /extensions/{name} — unload an extension by id
        extensions.MapDelete("/extensions/{name}", DeleteExtensionAsync)
            .WithName("Extensions.Delete")
            .WithSummary("Unload and unregister an extension by id.")
            .Produces<ExtensionDeleteResponse>(StatusCodes.Status200OK)
            .Produces<ExtensionDeleteResponse>(StatusCodes.Status404NotFound);

        // GET /extensions — list all loaded extensions
        extensions.MapGet("/extensions", ListExtensionsAsync)
            .WithName("Extensions.List")
            .WithSummary("List all currently loaded extensions.")
            .Produces<ExtensionListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /extensions/{name} — fetch a single loaded extension
        extensions.MapGet("/extensions/{name}", GetExtensionByNameAsync)
            .WithName("Extensions.Get")
            .WithSummary("Fetch a single loaded extension by id.")
            .Produces<ExtensionQueryResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /agents/{id}/extensions — per-agent extension chain diagnostic
        extensions.MapGet("/agents/{id}/extensions", GetAgentExtensionsAsync)
            .WithName("Agents.Extensions")
            .WithSummary("List extension handlers visible to an agent with scope match diagnostics.")
            .Produces<AgentExtensionChainResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // GET /extensions/{name}/metrics — rolling-window latency metrics (EXO-14)
        extensions.MapGet("/extensions/{name}/metrics", GetExtensionMetricsAsync)
            .WithName("Extensions.Metrics")
            .WithSummary("Return p50/p95 latency metrics for all handlers of an extension (5-minute rolling window).")
            .Produces<ExtensionMetricsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder;
    }

    private static async Task<IResult> ApplyExtensionAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return Results.Problem("Expected multipart/form-data.", statusCode: StatusCodes.Status415UnsupportedMediaType);

        var form = await http.Request.ReadFormAsync(ct).ConfigureAwait(false);

        var manifestFile = form.Files.GetFile("manifest");
        if (manifestFile is null)
            return Results.Json(
                new ExtensionApplyResponse(string.Empty, ExtensionApplyStatus.ValidationFailed, null,
                    "The 'manifest' file part is required."),
                statusCode: StatusCodes.Status400BadRequest);

        string yaml;
        using (var reader = new System.IO.StreamReader(manifestFile.OpenReadStream()))
        {
            yaml = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var deserializer = http.RequestServices.GetService<ExtensionManifestYamlDeserializer>();
        if (deserializer is null)
            return Results.Json(
                new ExtensionApplyResponse(string.Empty, ExtensionApplyStatus.ValidationFailed, null,
                    "Extension runtime is not registered. Call AddVaisExtensions() in your service configuration."),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        ExtensionManifest manifest;
        try
        {
            manifest = deserializer.Deserialize(yaml);
        }
        catch (Exception ex)
        {
            return Results.Json(
                new ExtensionApplyResponse(string.Empty, ExtensionApplyStatus.ValidationFailed, null,
                    $"Failed to parse extension manifest: {ex.Message}"),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
            return Results.Json(
                new ExtensionApplyResponse(string.Empty, ExtensionApplyStatus.ValidationFailed, null,
                    "manifest.id is required."),
                statusCode: StatusCodes.Status400BadRequest);

        // Authorize the mutation before any side effects. Apply is create-or-update; gate on
        // ExtensionUpdate as the representative apply verb (per-verb RBAC granularity is Plan B Phase 2).
        var denied = await ControlPlaneEndpointGate
            .CheckAsync(http, PolicyOperation.ExtensionUpdate, manifest.Id, manifest.Version, ct).ConfigureAwait(false);
        if (denied is not null) return denied;

        // High-risk approval gate (Extension runs code). Held applies return 202 + a requestId
        // and mutate nothing until an operator approves the exact manifest (the raw YAML body).
        var extApprovalGate = http.RequestServices.GetService<IApprovalGate>();
        if (extApprovalGate is not null)
        {
            var requestedBy = http.RequestServices.GetService<IAgentContextAccessor>()?.Current.UserId ?? "anonymous";
            try
            {
                await extApprovalGate.EnsureApprovedAsync("Extension", manifest.Id, yaml, requestedBy, ct).ConfigureAwait(false);
            }
            catch (ApprovalRequiredException are)
            {
                return ProblemDetailsMapping.ToResult(are, http.Request.Path, manifest.Id, PolicyOperation.ExtensionUpdate);
            }
        }

        // Hot-seam guard: container extensions on hot seams require explicit acknowledgment.
        var hotSeamGuard = http.RequestServices.GetService<HotSeamGuard>() ?? HotSeamGuard.Default;
        var violations = hotSeamGuard.Evaluate(manifest);
        if (violations.Count > 0)
        {
            var acceptHeader = http.Request.Headers.TryGetValue("X-Vais-Accept-Latency-Cost", out var hv)
                && string.Equals(hv.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (!acceptHeader)
            {
                var detail = string.Join("; ", violations.Select(v => $"{v.HandlerId} on seam '{v.Seam}'"));
                return Results.Json(
                    new ExtensionApplyResponse(manifest.Id, ExtensionApplyStatus.ValidationFailed, null,
                        $"Hot-seam guard: the following handlers are on latency-sensitive seams and require explicit acknowledgment: {detail}. " +
                        "Re-apply with 'X-Vais-Accept-Latency-Cost: true' or '--accept-latency-cost'."),
                    statusCode: 412);
            }
        }

        // Route by host type.
        if (string.Equals(manifest.Spec.Host, "container", StringComparison.OrdinalIgnoreCase))
        {
            var containerManager = http.RequestServices.GetService<ContainerExtensionLifecycleManager>();
            if (containerManager is null)
                return Results.Json(
                    new ExtensionApplyResponse(manifest.Id, ExtensionApplyStatus.ValidationFailed, null,
                        "Container extension support is not registered. Call AddVaisExtensions() in your service configuration."),
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var containerResult = await containerManager.ApplyAsync(manifest, ct).ConfigureAwait(false);
            return ExtensionReloadResultToHttp(containerResult, manifest.Id);
        }

        var reloader = http.RequestServices.GetService<IExtensionReloader>();
        if (reloader is null)
            return Results.Json(
                new ExtensionApplyResponse(manifest.Id, ExtensionApplyStatus.ValidationFailed, null,
                    "Extension runtime is not registered. Call AddVaisExtensions() in your service configuration."),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var dllFile = form.Files.GetFile("dll");
        Stream? dllStream = dllFile?.OpenReadStream();

        try
        {
            var result = await reloader.ReloadAsync(manifest, dllStream, ct).ConfigureAwait(false);
            return ExtensionReloadResultToHttp(result, manifest.Id);
        }
        finally
        {
            if (dllStream is not null)
                await dllStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<IResult> DeleteExtensionAsync(string name, HttpContext http, CancellationToken ct)
    {
        var reloader = http.RequestServices.GetService<IExtensionReloader>();
        if (reloader is null)
            return Results.Json(
                new ExtensionDeleteResponse(name, ExtensionDeleteStatus.NotFound),
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var denied = await ControlPlaneEndpointGate
            .CheckAsync(http, PolicyOperation.ExtensionEvict, name, version: null, ct).ConfigureAwait(false);
        if (denied is not null) return denied;

        var result = await reloader.UnloadAsync(name, ct).ConfigureAwait(false);

        return result.Status switch
        {
            ExtensionUnloadStatus.Success =>
                Results.Ok(new ExtensionDeleteResponse(name, ExtensionDeleteStatus.Success)),
            ExtensionUnloadStatus.NotFound =>
                Results.Json(
                    new ExtensionDeleteResponse(name, ExtensionDeleteStatus.NotFound),
                    statusCode: StatusCodes.Status404NotFound),
            _ =>
                Results.Json(
                    new ExtensionDeleteResponse(name, ExtensionDeleteStatus.NotFound),
                    statusCode: StatusCodes.Status404NotFound),
        };
    }

    private static Task<IResult> ListExtensionsAsync(HttpContext http, CancellationToken ct)
    {
        var registry = http.RequestServices.GetService<ExtensionHandlerRegistry>();
        if (registry is null)
            return Task.FromResult(Results.Problem(
                "Extension runtime not registered.", statusCode: StatusCodes.Status503ServiceUnavailable));

        var items = registry.Snapshot().Values
            .Select(ToExtensionInfo)
            .ToArray();
        return Task.FromResult(Results.Ok(new ExtensionListResponse(items)));
    }

    private static Task<IResult> GetExtensionByNameAsync(string name, HttpContext http, CancellationToken ct)
    {
        var registry = http.RequestServices.GetService<ExtensionHandlerRegistry>();
        if (registry is null)
            return Task.FromResult(Results.Problem(
                "Extension runtime not registered.", statusCode: StatusCodes.Status503ServiceUnavailable));

        var snapshot = registry.Snapshot();
        if (!snapshot.TryGetValue(name, out var descriptor))
            return Task.FromResult(Results.NotFound(new { error = $"extension '{name}' not loaded" }));

        return Task.FromResult(Results.Ok(
            new ExtensionQueryResponse(ToExtensionInfo(descriptor), descriptor.Manifest)));
    }

    private static Task<IResult> GetExtensionMetricsAsync(string name, HttpContext http, CancellationToken ct)
    {
        var metrics = http.RequestServices.GetService<IExtensionMetricsService>();
        if (metrics is null)
            return Task.FromResult(Results.Problem(
                "Extension metrics service not registered.", statusCode: StatusCodes.Status503ServiceUnavailable));

        var result = metrics.GetMetrics(name);
        if (result is null)
            return Task.FromResult(Results.NotFound(new { error = $"no metrics recorded for extension '{name}'" }));

        var items = result.Handlers
            .Select(h => new ExtensionHandlerMetricsItem(
                h.HandlerId, h.Seam, h.P50Seconds, h.P95Seconds, h.ErrorRate, h.TotalInvocations))
            .ToArray();
        return Task.FromResult(Results.Ok(new ExtensionMetricsResponse(result.ExtensionId, items)));
    }

    private static Task<IResult> GetAgentExtensionsAsync(string id, HttpContext http, CancellationToken ct)
    {
        var registry = http.RequestServices.GetService<ExtensionHandlerRegistry>();
        if (registry is null)
            return Task.FromResult(Results.Problem(
                "Extension runtime not registered.", statusCode: StatusCodes.Status503ServiceUnavailable));

        var entries = new List<AgentExtensionEntry>();
        foreach (var descriptor in registry.Snapshot().Values)
        {
            var scope = descriptor.Manifest.Spec.Scope;
            var matched = ExtensionScopeMatcher.Matches(scope, manifest: null, agentId: id);
            var scopeSummary = BuildScopeSummary(scope);

            foreach (var h in descriptor.Handlers)
            {
                entries.Add(new AgentExtensionEntry(
                    descriptor.ExtensionId, h.HandlerId, h.Seam,
                    h.Priority, h.FailureMode, matched, scopeSummary));
            }
        }

        entries.Sort((a, b) =>
        {
            var seam = string.Compare(a.Seam, b.Seam, StringComparison.Ordinal);
            if (seam != 0) return seam;
            var match = b.MatchedScope.CompareTo(a.MatchedScope); // matched first
            if (match != 0) return match;
            return a.Priority.CompareTo(b.Priority);
        });

        return Task.FromResult(Results.Ok(new AgentExtensionChainResponse(id, entries)));
    }

    private static ExtensionInfo ToExtensionInfo(ExtensionDescriptor d) =>
        new(d.ExtensionId, d.Version, d.Manifest.Spec.Host,
            d.Handlers.Select(h => new ExtensionHandlerInfo(h.HandlerId, h.Seam, h.Priority, h.FailureMode))
                      .ToArray());

    private static string BuildScopeSummary(ExtensionScope? scope)
    {
        if (scope is null) return "cluster-wide";
        var parts = new List<string>();
        if (scope.AgentIds is { Count: > 0 })
            parts.Add($"agentIds=[{string.Join(",", scope.AgentIds)}]");
        if (scope.Workspaces is { Count: > 0 })
            parts.Add($"workspaces=[{string.Join(",", scope.Workspaces)}]");
        if (scope.Selector is not null)
            parts.Add($"selector=[{string.Join(",", scope.Selector.MatchLabels.Select(kv => $"{kv.Key}={kv.Value}"))}]");
        return parts.Count > 0 ? string.Join("; ", parts) : "cluster-wide";
    }

    private static IResult ExtensionReloadResultToHttp(ExtensionReloadResult result, string manifestId)
    {
        var handlers = result.NewDescriptor?.Handlers
            .Select(h => h.HandlerId)
            .ToArray();
        var extensionId = result.NewDescriptor?.ExtensionId
            ?? result.OldDescriptor?.ExtensionId
            ?? manifestId;
        var isNew = result.OldDescriptor is null;

        return result.Status switch
        {
            ExtensionReloadStatus.Success when isNew =>
                Results.Created((string?)null,
                    new ExtensionApplyResponse(extensionId, ExtensionApplyStatus.Created, handlers, null)),
            ExtensionReloadStatus.Success =>
                Results.Ok(
                    new ExtensionApplyResponse(extensionId, ExtensionApplyStatus.Success, handlers, null)),
            ExtensionReloadStatus.AbiMismatch =>
                Results.Json(
                    new ExtensionApplyResponse(extensionId, ExtensionApplyStatus.AbiMismatch, null,
                        result.FailureException?.Message ?? "ABI version mismatch."),
                    statusCode: StatusCodes.Status400BadRequest),
            ExtensionReloadStatus.PriorityConflict =>
                Results.Json(
                    new ExtensionApplyResponse(extensionId, ExtensionApplyStatus.PriorityConflict, null,
                        result.FailureException?.Message ?? "Priority conflict detected."),
                    statusCode: StatusCodes.Status409Conflict),
            _ =>
                Results.Json(
                    new ExtensionApplyResponse(extensionId, ExtensionApplyStatus.LoadFailed, null,
                        result.FailureException?.Message ?? "Extension load failed."),
                    statusCode: StatusCodes.Status422UnprocessableEntity),
        };
    }

    // ── Eval suite endpoints (E1) ─────────────────────────────────────────────

    /// <summary>
    /// Mount the eval suite control-plane endpoints (E1).
    /// </summary>
    public static IEndpointRouteBuilder MapEvalSuiteControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = builder.MapGroup(prefix).WithTags("EvalSuites");

        group.MapPost("/eval-suites", EvalSuiteUpsertAsync)
            .WithName("EvalSuites.Upsert")
            .WithSummary("Register or overwrite an eval suite manifest.")
            .Accepts<EvalSuiteManifest>("application/json", "application/yaml")
            .Produces<EvalSuiteApplyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/eval-suites", EvalSuiteListAsync)
            .WithName("EvalSuites.List")
            .WithSummary("List registered eval suite manifests.")
            .Produces<EvalSuiteListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/eval-suites/{id}", EvalSuiteQueryAsync)
            .WithName("EvalSuites.Query")
            .WithSummary("Fetch an eval suite manifest by id.")
            .Produces<EvalSuiteQueryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapDelete("/eval-suites/{id}", EvalSuiteEvictAsync)
            .WithName("EvalSuites.Evict")
            .WithSummary("Remove an eval suite manifest.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return builder;
    }

    private static async Task<IResult> EvalSuiteUpsertAsync(HttpContext http, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IEvalSuiteRegistry>();
        var loader = http.RequestServices.GetRequiredService<JsonAgentGraphManifestLoader>();
        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        EvalSuiteManifest manifest;
        try
        {
            var resources = await loader.LoadAllResourcesFromStringAsync(body, ct).ConfigureAwait(false);
            var suites = resources.OfType<ManifestResource.EvalSuiteCase>().ToList();
            if (suites.Count != 1)
                return Results.BadRequest(new { error = $"POST /eval-suites accepts exactly one EvalSuite manifest; got {suites.Count}." });
            manifest = suites[0].Suite;
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or System.Text.Json.JsonException)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.EvalSuiteUpsert);
        }

        var denied = await ControlPlaneEndpointGate
            .CheckAsync(http, PolicyOperation.EvalSuiteUpsert, manifest.Id, manifest.Version, ct).ConfigureAwait(false);
        if (denied is not null) return denied;

        try
        {
            await registry.UpsertAsync(manifest, ct).ConfigureAwait(false);
            var handle = new EvalSuiteHandle(manifest.Id, manifest.Version);
            return Results.Ok(new EvalSuiteApplyResponse(handle, Array.Empty<ApplyDiagnostic>()));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, manifest.Id, PolicyOperation.EvalSuiteUpsert);
        }
    }

    private static async Task<IResult> EvalSuiteListAsync(HttpContext http, int? limit, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IEvalSuiteRegistry>();
        try
        {
            var take = Math.Clamp(limit ?? 50, 1, 500);
            var items = new List<EvalSuiteManifest>();
            await foreach (var m in registry.ListAsync(ct: ct).ConfigureAwait(false))
            {
                items.Add(m);
                if (items.Count >= take) break;
            }
            return Results.Ok(new EvalSuiteListResponse(items, NextCursor: null));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, operation: PolicyOperation.EvalSuiteQuery);
        }
    }

    private static async Task<IResult> EvalSuiteQueryAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IEvalSuiteRegistry>();
        try
        {
            var manifest = await registry.GetAsync(id, version, ct).ConfigureAwait(false);
            if (manifest is null)
                return Results.NotFound(new { error = $"eval-suite '{id}' not found" });
            var handle = new EvalSuiteHandle(id, manifest.Version);
            return Results.Ok(new EvalSuiteQueryResponse(manifest, handle));
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.EvalSuiteQuery);
        }
    }

    private static async Task<IResult> EvalSuiteEvictAsync(HttpContext http, string id, string? version, CancellationToken ct)
    {
        var registry = http.RequestServices.GetRequiredService<IEvalSuiteRegistry>();

        var denied = await ControlPlaneEndpointGate
            .CheckAsync(http, PolicyOperation.EvalSuiteEvict, id, version, ct).ConfigureAwait(false);
        if (denied is not null) return denied;

        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
                return Results.NotFound(new { error = $"eval-suite '{id}' not found" });
            await registry.RemoveAsync(id, resolvedVersion, ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id);
        }
    }

    // ── Diagnostics endpoints (v0.36) ─────────────────────────────────────────

    /// <summary>
    /// Mount the diagnostics control-plane endpoints (v0.36).
    /// </summary>
    public static IEndpointRouteBuilder MapDiagnosticsControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = builder.MapGroup(prefix).WithTags("Diagnostics");

        group.MapGet("/diagnostics/spans", DiagnosticsSpansAsync)
            .WithName("Diagnostics.Spans")
            .WithSummary("Returns recent OTel spans from the in-process circular buffer. Requires VAIS_DIAG_SPAN_BUFFER=true.")
            .Produces<DiagSpanListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/diagnostics/filter-status", DiagnosticsFilterStatusAsync)
            .WithName("Diagnostics.FilterStatus")
            .WithSummary("Returns per-interface call counters recorded by OrleansOutgoingActivityFilter.")
            .Produces<FilterStatusResponse>(StatusCodes.Status200OK);

        return builder;
    }

    private static IResult DiagnosticsSpansAsync(
        HttpContext http,
        string? source = null,
        int limit = 100)
    {
        var buffer = http.RequestServices.GetService<IDiagSpanBuffer>();
        if (buffer is null || !buffer.IsEnabled)
            return Results.Problem(
                title: "Span buffer not configured",
                detail: "Set VAIS_DIAG_SPAN_BUFFER=true to enable the in-process span buffer.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "urn:vais-agents:diag-span-buffer-not-configured");

        var limitClamped = Math.Clamp(limit, 1, 1000);
        var spans = buffer.GetSpans(source, limitClamped);
        return Results.Ok(new DiagSpanListResponse(spans));
    }

    private static IResult DiagnosticsFilterStatusAsync(HttpContext http)
    {
        var tracker = http.RequestServices.GetRequiredService<IFilterStatusTracker>();
        var snapshot = tracker.GetSnapshot();
        var total = snapshot.Sum(static e => e.WithActivity + e.WithoutActivity);
        return Results.Ok(new FilterStatusResponse(snapshot, total));
    }

    // ── Eval run endpoints (EH-13) ────────────────────────────────────────────

    /// <summary>Mount eval run control-plane endpoints.</summary>
    public static IEndpointRouteBuilder MapEvalRunControlPlane(this IEndpointRouteBuilder builder, string prefix = "/v1")
    {
        ArgumentNullException.ThrowIfNull(builder);

        var group = builder.MapGroup(prefix).WithTags("EvalRuns");

        group.MapPost("/eval-suites/{name}/runs", EvalRunStartAsync)
            .WithName("EvalRuns.Start")
            .WithSummary("Start a new eval run for the named suite.")
            .Produces<EvalRunStartResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/eval-runs", EvalRunListAsync)
            .WithName("EvalRuns.List")
            .WithSummary("List eval runs (workspace-scoped).")
            .Produces<EvalRunListResponse>(StatusCodes.Status200OK);

        group.MapGet("/eval-runs/{evalRunId}", EvalRunGetAsync)
            .WithName("EvalRuns.Get")
            .WithSummary("Get eval run detail including per-case results.")
            .Produces<Vais.Agents.Eval.EvalRunDetail>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/eval-runs/{evalRunId}/cancel", EvalRunCancelAsync)
            .WithName("EvalRuns.Cancel")
            .WithSummary("Request cancellation of a running eval run.")
            .Produces(StatusCodes.Status202Accepted);

        group.MapGet("/eval-runs/{evalRunId}/stream", EvalRunStreamAsync)
            .WithName("EvalRuns.Stream")
            .WithSummary("SSE stream of eval run progress events.");

        group.MapGet("/eval-runs/diff", EvalRunDiffAsync)
            .WithName("EvalRuns.Diff")
            .WithSummary("Side-by-side diff of two eval runs joined by case id.")
            .Produces<EvalDiffResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return builder;
    }

    private static async Task<IResult> EvalRunStartAsync(
        HttpContext http, string name, CancellationToken ct)
    {
        var manager = http.RequestServices.GetService<Vais.Agents.Eval.IEvalRunLifecycleManager>();
        if (manager is null)
            return Results.Problem("IEvalRunLifecycleManager is not registered.", statusCode: StatusCodes.Status503ServiceUnavailable,
                type: "urn:vais-agents:eval-run-manager-not-configured");
        try
        {
            var workspace = ResolveWorkspace(http);
            var evalRunId = await manager.StartRunAsync(name, workspace, ct).ConfigureAwait(false);
            return Results.Accepted(value: new EvalRunStartResponse(evalRunId));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound(new { error = $"eval-suite '{name}' not found" });
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, name);
        }
    }

    private static async Task<IResult> EvalRunListAsync(
        HttpContext http, string? suite = null, int limit = 50, string? source = null, CancellationToken ct = default)
    {
        var manager = http.RequestServices.GetService<Vais.Agents.Eval.IEvalRunLifecycleManager>();
        if (manager is null) return Results.Ok(new EvalRunListResponse(Array.Empty<Vais.Agents.Eval.EvalRunSummary>()));

        var items = await manager.ListRunsAsync(suite, Math.Clamp(limit, 1, 200), source, ct).ConfigureAwait(false);
        return Results.Ok(new EvalRunListResponse(items));
    }

    private static async Task<IResult> EvalRunGetAsync(
        HttpContext http, string evalRunId, CancellationToken ct)
    {
        var manager = http.RequestServices.GetService<Vais.Agents.Eval.IEvalRunLifecycleManager>();
        if (manager is null) return Results.NotFound();

        var detail = await manager.GetRunDetailAsync(evalRunId, ct).ConfigureAwait(false);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> EvalRunCancelAsync(
        HttpContext http, string evalRunId, CancellationToken ct)
    {
        var manager = http.RequestServices.GetService<Vais.Agents.Eval.IEvalRunLifecycleManager>();
        if (manager is null) return Results.Accepted();
        await manager.CancelRunAsync(evalRunId, ct).ConfigureAwait(false);
        return Results.Accepted();
    }

    private static async Task EvalRunStreamAsync(HttpContext http, string evalRunId, CancellationToken ct)
    {
        var bus = http.RequestServices.GetService<IAgentEventBus>();
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers["Cache-Control"] = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);

        if (bus is null)
        {
            // No event bus — emit a single run-completed so the client doesn't hang.
            await WriteEventAsync(http, "run-completed", $"{{\"evalRunId\":\"{evalRunId}\",\"progressKind\":\"run-completed\"}}", ct).ConfigureAwait(false);
            return;
        }

        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        IDisposable sub = bus.Subscribe(async (evt, innerCt) =>
        {
            if (evt is not EvalRunProgress prog || prog.EvalRunId != evalRunId) return;
            var (eventName, json) = AgentEventSerializer.Serialize(prog);
            await channel.Writer.WriteAsync($"event: {eventName}\ndata: {json}\n\n", innerCt).ConfigureAwait(false);
            if (prog.ProgressKind == "run-completed") channel.Writer.TryComplete();
        });

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                await http.Response.WriteAsync(frame, cts.Token).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        finally
        {
            sub.Dispose();
        }
    }

    private static Task WriteEventAsync(HttpContext http, string eventName, string json, CancellationToken ct)
        => http.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct);

    private static async Task<IResult> EvalRunDiffAsync(
        HttpContext http, string? a, string? b, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return Results.Problem("Query params 'a' and 'b' (eval run ids) are required.",
                statusCode: StatusCodes.Status400BadRequest);

        var manager = http.RequestServices.GetService<Vais.Agents.Eval.IEvalRunLifecycleManager>();
        if (manager is null)
            return Results.Problem("IEvalRunLifecycleManager not registered.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var baseDetail = await manager.GetRunDetailAsync(a, ct).ConfigureAwait(false);
        var candidateDetail = await manager.GetRunDetailAsync(b, ct).ConfigureAwait(false);

        if (baseDetail is null)
            return Results.NotFound(new { error = $"eval run '{a}' not found" });
        if (candidateDetail is null)
            return Results.NotFound(new { error = $"eval run '{b}' not found" });

        var baseById = baseDetail.Cases.ToDictionary(c => c.CaseId, c => c);
        var candidateById = candidateDetail.Cases.ToDictionary(c => c.CaseId, c => c);

        var allCaseIds = baseById.Keys.Union(candidateById.Keys).OrderBy(x => x);

        var caseDiffs = new List<EvalCaseDiff>();
        foreach (var caseId in allCaseIds)
        {
            baseById.TryGetValue(caseId, out var baseCase);
            candidateById.TryGetValue(caseId, out var candidateCase);

            var assertionCount = Math.Max(
                baseCase?.AssertionResults.Count ?? 0,
                candidateCase?.AssertionResults.Count ?? 0);

            var assertDiffs = new List<EvalAssertionDiff>(assertionCount);
            for (var i = 0; i < assertionCount; i++)
            {
                var bA = baseCase?.AssertionResults.ElementAtOrDefault(i);
                var cA = candidateCase?.AssertionResults.ElementAtOrDefault(i);
                var kind = bA?.Kind ?? cA?.Kind ?? $"assertion-{i}";
                var scoreDelta = (cA?.Score ?? 0.0) - (bA?.Score ?? 0.0);
                assertDiffs.Add(new EvalAssertionDiff(i, kind, bA?.Status, bA?.Score, cA?.Status, cA?.Score, scoreDelta));
            }

            caseDiffs.Add(new EvalCaseDiff(caseId, baseCase?.Status, candidateCase?.Status, assertDiffs));
        }

        return Results.Ok(new EvalDiffResponse(a, b, caseDiffs));
    }
}
