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

    /// <summary>Budget cap exceeded mid-invocation.</summary>
    public const string BudgetExceededType = TypePrefix + "budget-exceeded";

    /// <summary>Agent raised an <see cref="AgentInterrupt"/>; caller should switch to streaming or resume.</summary>
    public const string InterruptPendingType = TypePrefix + "interrupt-pending";

    /// <summary>Catch-all for mid-verb exceptions that don't map to a specific type URN.</summary>
    public const string BackendUnavailableType = TypePrefix + "backend-unavailable";

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
        if (ex is AgentManifestValidationException validation)
        {
            pd.Extensions["errors"] = validation.Errors.ToArray();
        }
        return Results.Problem(pd);
    }

    private static (int Status, string Type, string Title) Classify(Exception ex) => ex switch
    {
        AgentManifestValidationException => (StatusCodes.Status400BadRequest, ManifestInvalidType, "Manifest validation failed"),
        AgentPolicyDeniedException => (StatusCodes.Status403Forbidden, PolicyDeniedType, "Policy denied"),
        AgentBudgetExceededException => (StatusCodes.Status429TooManyRequests, BudgetExceededType, "Budget exceeded"),
        AgentInterruptedException => (StatusCodes.Status409Conflict, InterruptPendingType, "Interrupt pending"),
        ArgumentException => (StatusCodes.Status400BadRequest, TypePrefix + "bad-request", "Bad request"),
        InvalidOperationException when ex.Message.Contains("Unknown agent", StringComparison.OrdinalIgnoreCase)
            => (StatusCodes.Status404NotFound, AgentNotFoundType, "Agent not found"),
        _ => (StatusCodes.Status503ServiceUnavailable, BackendUnavailableType, "Backend unavailable"),
    };
}
