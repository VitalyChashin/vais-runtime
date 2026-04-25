// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vais.Agents.Control.Manifests;
using Vais.Agents.Runtime.Plugins;

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
            .Produces<AgentHandle>(StatusCodes.Status201Created)
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
            .Produces<AgentHandle>(StatusCodes.Status200OK)
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

        // POST /graphs — Create
        graphs.MapPost("/graphs", GraphCreateAsync)
            .WithName("Graphs.Create")
            .WithSummary("Register a graph manifest, making it available for invocation.")
            .Accepts<AgentGraphManifest>("application/json", "application/yaml")
            .Produces<AgentGraphHandle>(StatusCodes.Status201Created)
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
            .Produces<AgentGraphHandle>(StatusCodes.Status200OK)
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

        return builder;
    }

    private static IResult PluginListAsync(HttpContext http)
    {
        var registry = http.RequestServices.GetService<IPluginHandlerRegistry>();
        if (registry is null)
            return Results.Ok(new PluginListResponse(Array.Empty<PluginInfo>()));

        var items = registry.Plugins
            .Select(d => new PluginInfo(d.Name, d.AssemblyPath, d.TargetApiVersion, d.Handlers, d.LoadedViaAttribute))
            .ToArray();
        return Results.Ok(new PluginListResponse(items));
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        IAgentLifecycleManager manager,
        IAgentManifestLoader loader,
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
            var handle = await manager.CreateAsync(manifest, ct).ConfigureAwait(false);
            return Results.Created($"{http.Request.PathBase}{http.Request.Path}/{manifest.Id}", handle);
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
            var newHandle = await manager.UpdateAsync(currentHandle, manifest, ct).ConfigureAwait(false);
            return Results.Ok(newHandle);
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

        try
        {
            var resolvedVersion = version ?? (await registry.GetAsync(id, version: null, ct).ConfigureAwait(false))?.Version;
            if (resolvedVersion is null)
            {
                return Results.NotFound(new { error = $"agent '{id}' not found" });
            }
            var handle = new AgentHandle(id, resolvedVersion);
            var result = await manager.InvokeAsync(handle, request, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.Invoke);
        }
    }

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
        var principal = http.User?.Identity?.IsAuthenticated == true
            ? new AgentContext(
                UserId: http.User.FindFirst("sub")?.Value,
                TenantId: http.User.FindFirst("tenant_id")?.Value ?? http.User.FindFirst("tid")?.Value)
            : AgentContext.Empty;

        var producerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in streamable.StreamAsync(request.Text, principal, writerCt).ConfigureAwait(false))
                {
                    var (eventName, dataJson) = AgentEventSerializer.Serialize(evt);
                    var frame = $"event: {eventName}\ndata: {dataJson}\n\n";
                    await channel.Writer.WriteAsync(frame, writerCt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* cooperative shutdown */ }
            catch (Exception ex)
            {
                // Mid-stream exception the agent didn't translate to TurnFailed — emit a
                // synthetic turn.failed event so the client sees a clean terminal.
                var turnFailed = new TurnFailed(DateTimeOffset.UtcNow, principal, ex.GetType().Name, ex.Message, TimeSpan.Zero);
                var (eventName, dataJson) = AgentEventSerializer.Serialize(turnFailed);
                var frame = $"event: {eventName}\ndata: {dataJson}\n\n";
                try { await channel.Writer.WriteAsync(frame, writerCt).ConfigureAwait(false); }
                catch { /* swallow — writer already gone */ }
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
        }
    }

    private static async Task WriteProblemAsync(HttpContext http, IResult result)
    {
        await result.ExecuteAsync(http).ConfigureAwait(false);
    }

    // ── Graph handlers (v0.19) ─────────────────────────────────────────────

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
            var handle = await manager.CreateAsync(manifest, ct).ConfigureAwait(false);
            return Results.Created($"{http.Request.PathBase}{http.Request.Path}/{manifest.Id}", handle);
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
            var currentHandle = new AgentGraphHandle(id, existingVersion);
            var newHandle = await manager.UpdateAsync(currentHandle, newManifest, ct).ConfigureAwait(false);
            return Results.Ok(newHandle);
        }
        catch (Exception ex)
        {
            return ProblemDetailsMapping.ToResult(ex, http.Request.Path, id, PolicyOperation.GraphUpdate);
        }
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
}
