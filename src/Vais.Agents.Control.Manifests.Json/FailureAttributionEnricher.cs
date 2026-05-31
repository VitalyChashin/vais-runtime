// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Observability-kind <see cref="ToolGatewayMiddleware"/> that, when a tool call fails,
/// emits a <c>failure.attribution</c> trajectory event to <see cref="IInterceptorTee"/> with
/// the deployment-grounded attribution from the bound <see cref="FailureAttributionArtifact"/>.
/// Read-only (P12): does not modify the tool call outcome or any bus event; the tee write is
/// fire-and-forget best-effort. RunHealth signal attribution is stamped by the subscriber
/// and aggregator independently.
/// </summary>
public sealed class FailureAttributionEnricher : ToolGatewayMiddleware
{
    private readonly IInterceptorTee? _tee;
    private readonly FailureAttributionArtifact? _artifact;
    private readonly IFailureOntologyCatalog? _catalog;

    /// <summary>Creates the enricher for a per-virtual-server or per-agent binding.</summary>
    public FailureAttributionEnricher(
        IInterceptorTee? tee,
        FailureAttributionArtifact? artifact,
        IFailureOntologyCatalog? catalog)
    {
        _tee = tee;
        _artifact = artifact;
        _catalog = catalog;
    }

    /// <inheritdoc/>
    public override InterceptorKind Kind => InterceptorKind.Observability;

    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        var outcome = await next().ConfigureAwait(false);

        if (_tee is null || string.IsNullOrEmpty(outcome.Error))
            return outcome;

        var annotation = _artifact?.ForTool(context.ToolName);
        var conceptName = annotation?.Concept
            ?? _catalog?.FromSignalKind(RunHealthSignalKind.McpError)?.Name
            ?? "McpToolError";

        var attributionPath = BuildAttributionPath(context.ToolName, annotation, context.AgentContext);

        var payload = new FailureAttributionPayload(
            ToolName: context.ToolName,
            ConceptName: conceptName,
            AttributionPath: attributionPath,
            ErrorType: outcome.Error,
            Tags: annotation?.Tags);

        var evt = new InterceptorTeeEvent
        {
            EventName = "failure.attribution",
            Context = new FailureAttributionInterceptionContext
            {
                Operation = OntologyOperation.Call,
                AgentContext = context.AgentContext,
                Binding = null,
            },
            Payload = payload,
        };

        try
        {
            await _tee.EmitAsync(evt, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort; enricher must not affect the tool dispatch result.
        }

        return outcome;
    }

    private static string BuildAttributionPath(
        string toolName,
        FailureToolAnnotation? annotation,
        AgentContext agentContext)
    {
        var agentId = agentContext.AgentName;
        var mcpServerId = annotation?.McpServerId;

        if (!string.IsNullOrEmpty(agentId) && !string.IsNullOrEmpty(mcpServerId))
            return $"{agentId}/{mcpServerId}/{toolName}";
        if (!string.IsNullOrEmpty(agentId))
            return $"{agentId}/{toolName}";
        if (!string.IsNullOrEmpty(mcpServerId))
            return $"{mcpServerId}/{toolName}";
        return toolName;
    }
}

/// <summary>
/// Payload for <c>failure.attribution</c> trajectory events emitted by
/// <see cref="FailureAttributionEnricher"/>.
/// </summary>
/// <param name="ToolName">The tool that failed.</param>
/// <param name="ConceptName">Failure concept from the artifact or base catalog.</param>
/// <param name="AttributionPath">Deployment-grounded path: <c>{agentId}/{mcpServerId}/{toolName}</c> or shorter.</param>
/// <param name="ErrorType">Exception/error type from the tool outcome.</param>
/// <param name="Tags">Diagnostic tags from the artifact annotation.</param>
public sealed record FailureAttributionPayload(
    string ToolName,
    string ConceptName,
    string AttributionPath,
    string? ErrorType,
    IReadOnlyList<string>? Tags);

/// <summary>
/// <see cref="InterceptionContext"/> subtype for <c>failure.attribution</c> trajectory events.
/// </summary>
public sealed class FailureAttributionInterceptionContext : InterceptionContext
{
}
