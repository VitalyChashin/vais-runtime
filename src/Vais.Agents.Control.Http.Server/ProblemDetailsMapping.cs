// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Maps control-plane exceptions to RFC 7807 Problem Details responses with
/// stable <c>urn:vais-agents:*</c> type URNs. Consumers (both the shipped client
/// and third-party tooling) can pattern-match on <see cref="ProblemDetails.Type"/>
/// for typed error handling without depending on status-code flavour quirks.
/// </summary>
public static class ProblemDetailsMapping
{
    /// <summary>RFC 7807 type URN prefix for every control-plane problem.</summary>
    public const string TypePrefix = "urn:vais-agents:";

    /// <summary>Manifest failed validation — id/version/schema rules etc.</summary>
    public const string ManifestInvalidType = TypePrefix + "manifest-invalid";

    /// <summary>Unknown agent handle on Query / Invoke / Signal / Cancel / Evict / Update.</summary>
    public const string AgentNotFoundType = TypePrefix + "agent-not-found";

    /// <summary>Policy engine denied the verb.</summary>
    public const string PolicyDeniedType = TypePrefix + "policy-denied";

    /// <summary>A high-risk mutation is held pending operator approval (202).</summary>
    public const string ApprovalRequiredType = TypePrefix + "approval-required";

    /// <summary>Budget cap exceeded mid-invocation.</summary>
    public const string BudgetExceededType = TypePrefix + "budget-exceeded";

    /// <summary>Agent raised an <see cref="AgentInterrupt"/>; caller should switch to streaming or resume.</summary>
    public const string InterruptPendingType = TypePrefix + "interrupt-pending";

    /// <summary>Catch-all for mid-verb exceptions that don't map to a specific type URN.</summary>
    public const string BackendUnavailableType = TypePrefix + "backend-unavailable";

    /// <summary>Client reused an <c>Idempotency-Key</c> with a different request body (v0.11).</summary>
    public const string IdempotencyMismatchType = TypePrefix + "idempotency-mismatch";

    /// <summary>Another request with the same <c>Idempotency-Key</c> is still executing (v0.11).</summary>
    public const string IdempotencyInFlightType = TypePrefix + "idempotency-in-flight";

    /// <summary>Requested streaming endpoint, but the target agent's runtime (e.g. Orleans proxy) doesn't implement <c>IStreamingAiAgent</c> (v0.12).</summary>
    public const string StreamingNotSupportedType = TypePrefix + "streaming-not-supported";

    // ── Graph URNs (v0.19) ──────────────────────────────────────────────────

    /// <summary>Graph handle not found — no such (id, version) in the registry.</summary>
    public const string GraphHandleNotFoundType = TypePrefix + "graph-handle-not-found";

    /// <summary>No run with the given run-id exists for the specified graph.</summary>
    public const string GraphRunNotFoundType = TypePrefix + "graph-run-not-found";

    /// <summary>A run with the same run-id is already in flight.</summary>
    public const string GraphRunConflictType = TypePrefix + "graph-run-conflict";

    /// <summary>Resume interrupt-id does not match the paused checkpoint.</summary>
    public const string GraphInterruptMismatchType = TypePrefix + "graph-interrupt-mismatch";

    /// <summary>Run has already reached a terminal node; cannot be resumed or cancelled.</summary>
    public const string GraphAlreadyCompleteType = TypePrefix + "graph-already-complete";

    /// <summary>Graph exceeded its maxSteps ceiling.</summary>
    public const string GraphRecursionLimitType = TypePrefix + "graph-recursion-limit";

    /// <summary>Graph manifest failed structural validation.</summary>
    public const string GraphValidationFailedType = TypePrefix + "graph-validation-failed";

    // ── Gateway config URNs (v0.20) ─────────────────────────────────────────

    /// <summary>LLM gateway config handle not found.</summary>
    public const string LlmGatewayConfigHandleNotFoundType = TypePrefix + "llm-gateway-config-handle-not-found";

    /// <summary>LLM gateway config (id, version) already registered — use UpdateAsync to replace.</summary>
    public const string LlmGatewayConfigConflictType = TypePrefix + "llm-gateway-config-conflict";

    /// <summary>MCP gateway config handle not found.</summary>
    public const string McpGatewayConfigHandleNotFoundType = TypePrefix + "mcp-gateway-config-handle-not-found";

    /// <summary>MCP gateway config (id, version) already registered — use UpdateAsync to replace.</summary>
    public const string McpGatewayConfigConflictType = TypePrefix + "mcp-gateway-config-conflict";

    /// <summary>MCP server handle not found.</summary>
    public const string McpServerHandleNotFoundType = TypePrefix + "mcp-server-handle-not-found";

    /// <summary>MCP server (id, version) already registered — use UpdateAsync to replace.</summary>
    public const string McpServerConflictType = TypePrefix + "mcp-server-conflict";

    // ── Container plugin URNs (v0.21) ───────────────────────────────────────

    /// <summary>Container plugin handle not found.</summary>
    public const string ContainerPluginHandleNotFoundType = TypePrefix + "container-plugin-handle-not-found";

    /// <summary>Container plugin (id, version) already registered — use UpdateAsync to replace.</summary>
    public const string ContainerPluginConflictType = TypePrefix + "container-plugin-conflict";

    /// <summary>
    /// Translate a control-plane exception into an <see cref="IResult"/> carrying
    /// Problem Details with an appropriate status code + type URN.
    /// </summary>
    /// <param name="ex">Exception thrown by the lifecycle manager or a verb handler.</param>
    /// <param name="instance">Request path (used as <see cref="ProblemDetails.Instance"/>).</param>
    /// <param name="agentId">Target agent id when known (added as a Problem Detail extension).</param>
    /// <param name="operation">Lifecycle verb being attempted (extension).</param>
    public static IResult ToResult(Exception ex, string? instance = null, string? agentId = null, PolicyOperation? operation = null)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var (status, type, title) = Classify(ex);
        var pd = new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = ex.Message,
            Instance = instance,
        };
        if (agentId is not null) pd.Extensions["agentId"] = agentId;
        if (operation is PolicyOperation op) pd.Extensions["operation"] = op.ToString();
        if (ex is ApprovalRequiredException approval)
        {
            pd.Extensions["requestId"] = approval.RequestId;
            pd.Extensions["kind"] = approval.Kind;
            pd.Extensions["name"] = approval.Name;
            pd.Extensions["approvalStatus"] = "pending-approval";
        }
        if (ex is AgentManifestValidationException validation)
        {
            pd.Extensions["errors"] = validation.Errors.ToArray();
        }
        return Results.Problem(pd);
    }

    /// <summary>
    /// Build a 422 Problem Details response for an <c>Idempotency-Key</c> reuse
    /// with a different body. Called by the idempotency middleware when
    /// <see cref="IdempotencyBeginStatus.Mismatch"/> is observed.
    /// </summary>
    public static IResult IdempotencyMismatch(string key, string? existingFingerprint = null, string? instance = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var pd = new ProblemDetails
        {
            Type = IdempotencyMismatchType,
            Title = "Idempotency-Key reused with a different body",
            Status = StatusCodes.Status422UnprocessableEntity,
            Detail = $"Idempotency-Key '{key}' was already used with a different request body.",
            Instance = instance,
        };
        pd.Extensions["idempotencyKey"] = key;
        if (existingFingerprint is not null) pd.Extensions["existingFingerprint"] = existingFingerprint;
        return Results.Problem(pd);
    }

    /// <summary>
    /// Build a 501 Problem Details response when the requested streaming
    /// endpoint targets an agent whose runtime doesn't implement
    /// <c>IStreamingAiAgent</c> (e.g. Orleans-proxied agents in v0.12).
    /// </summary>
    public static IResult StreamingNotSupported(string agentId, string? instance = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        var pd = new ProblemDetails
        {
            Type = StreamingNotSupportedType,
            Title = "Streaming not supported",
            Status = StatusCodes.Status501NotImplemented,
            Detail = $"Agent '{agentId}' is hosted on a runtime that does not support streaming. " +
                     "Use POST /v1/agents/{id}/invoke for buffered responses.",
            Instance = instance,
        };
        pd.Extensions["agentId"] = agentId;
        return Results.Problem(pd);
    }

    /// <summary>
    /// Build a 409 Problem Details response for a concurrent request holding the
    /// same <c>Idempotency-Key</c>. Adds a <c>Retry-After</c> header hint.
    /// </summary>
    public static IResult IdempotencyInFlight(string key, TimeSpan? retryAfter = null, string? instance = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var pd = new ProblemDetails
        {
            Type = IdempotencyInFlightType,
            Title = "Idempotency-Key request in progress",
            Status = StatusCodes.Status409Conflict,
            Detail = $"Another request with Idempotency-Key '{key}' is currently executing. Retry after it completes.",
            Instance = instance,
        };
        pd.Extensions["idempotencyKey"] = key;
        var seconds = (int)Math.Max(1, (retryAfter ?? TimeSpan.FromSeconds(1)).TotalSeconds);
        return new RetryAfterResult(Results.Problem(pd), seconds);
    }

    private sealed class RetryAfterResult : IResult
    {
        private readonly IResult _inner;
        private readonly int _retryAfterSeconds;

        public RetryAfterResult(IResult inner, int retryAfterSeconds)
        {
            _inner = inner;
            _retryAfterSeconds = retryAfterSeconds;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["Retry-After"] = _retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return _inner.ExecuteAsync(httpContext);
        }
    }

    private static (int Status, string Type, string Title) Classify(Exception ex) => ex switch
    {
        AgentManifestValidationException => (StatusCodes.Status400BadRequest, ManifestInvalidType, "Manifest validation failed"),
        AgentPolicyDeniedException => (StatusCodes.Status403Forbidden, PolicyDeniedType, "Policy denied"),
        ApprovalRequiredException => (StatusCodes.Status202Accepted, ApprovalRequiredType, "Approval required"),
        AgentBudgetExceededException => (StatusCodes.Status429TooManyRequests, BudgetExceededType, "Budget exceeded"),
        AgentInterruptedException => (StatusCodes.Status409Conflict, InterruptPendingType, "Interrupt pending"),
        ArgumentException => (StatusCodes.Status400BadRequest, TypePrefix + "bad-request", "Bad request"),
        System.Text.Json.JsonException => (StatusCodes.Status400BadRequest, TypePrefix + "bad-request", "Invalid JSON in request body"),
        InvalidOperationException when ex.Message.Contains("Unknown agent", StringComparison.OrdinalIgnoreCase)
            => (StatusCodes.Status404NotFound, AgentNotFoundType, "Agent not found"),
        // ── Graph exceptions (v0.19) ──────────────────────────────────────
        GraphHandleNotFoundException => (StatusCodes.Status404NotFound, GraphHandleNotFoundType, "Graph not found"),
        GraphRunNotFoundException => (StatusCodes.Status404NotFound, GraphRunNotFoundType, "Graph run not found"),
        GraphRunConflictException => (StatusCodes.Status409Conflict, GraphRunConflictType, "Graph run conflict"),
        GraphInterruptMismatchException => (StatusCodes.Status409Conflict, GraphInterruptMismatchType, "Graph interrupt mismatch"),
        GraphAlreadyCompleteException => (StatusCodes.Status409Conflict, GraphAlreadyCompleteType, "Graph run already complete"),
        GraphRecursionException => (StatusCodes.Status422UnprocessableEntity, GraphRecursionLimitType, "Graph recursion limit exceeded"),
        // ── Gateway config exceptions (v0.20) ────────────────────────────
        LlmGatewayConfigHandleNotFoundException => (StatusCodes.Status404NotFound, LlmGatewayConfigHandleNotFoundType, "LLM gateway config not found"),
        LlmGatewayConfigConflictException => (StatusCodes.Status409Conflict, LlmGatewayConfigConflictType, "LLM gateway config already registered"),
        McpGatewayConfigHandleNotFoundException => (StatusCodes.Status404NotFound, McpGatewayConfigHandleNotFoundType, "MCP gateway config not found"),
        McpGatewayConfigConflictException => (StatusCodes.Status409Conflict, McpGatewayConfigConflictType, "MCP gateway config already registered"),
        McpServerHandleNotFoundException => (StatusCodes.Status404NotFound, McpServerHandleNotFoundType, "MCP server not found"),
        McpServerConflictException => (StatusCodes.Status409Conflict, McpServerConflictType, "MCP server already registered"),
        // ── Container plugin exceptions (v0.21) ──────────────────────────────
        ContainerPluginHandleNotFoundException => (StatusCodes.Status404NotFound, ContainerPluginHandleNotFoundType, "Container plugin not found"),
        ContainerPluginConflictException => (StatusCodes.Status409Conflict, ContainerPluginConflictType, "Container plugin already registered"),
        _ => (StatusCodes.Status503ServiceUnavailable, BackendUnavailableType, "Backend unavailable"),
    };
}
