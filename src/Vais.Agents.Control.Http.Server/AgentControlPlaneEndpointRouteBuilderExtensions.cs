// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vais.Agents.Control.Manifests;

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

        return builder;
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
}
