// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Observability.Langfuse;

/// <summary>
/// <see cref="IAgentFilter"/> that decorates the current OpenTelemetry <see cref="Activity"/>
/// with the <c>langfuse.*</c> tags the Langfuse UI reads (user id, session id, trace name,
/// metadata, tags). Source of data: the injected <see cref="IAgentContextAccessor"/>.
/// </summary>
/// <remarks>
/// This is the neutral replacement for VAIS's <c>LangfuseEnrichmentFilter</c> that was bound to
/// Semantic Kernel's <c>IFunctionInvocationFilter</c> and Orleans <c>RequestContext</c>.
/// Stack-neutral: registering it works identically for SK and MAF-backed agents.
/// Enrichment failures are logged and swallowed — this is non-critical telemetry.
/// </remarks>
public sealed class LangfuseEnrichmentFilter : IAgentFilter
{
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly LangfuseEnrichmentOptions _options;
    private readonly ILogger<LangfuseEnrichmentFilter> _logger;

    /// <summary>
    /// Create a new filter.
    /// </summary>
    /// <param name="contextAccessor">Accessor resolving the ambient <see cref="AgentContext"/>.</param>
    /// <param name="options">Optional overrides. <see cref="LangfuseEnrichmentOptions"/> defaults are used when null.</param>
    /// <param name="logger">Optional logger; a null-logger is used if none is supplied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contextAccessor"/> is null.</exception>
    public LangfuseEnrichmentFilter(
        IAgentContextAccessor contextAccessor,
        LangfuseEnrichmentOptions? options = null,
        ILogger<LangfuseEnrichmentFilter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contextAccessor);
        _contextAccessor = contextAccessor;
        _options = options ?? new LangfuseEnrichmentOptions();
        _logger = logger ?? NullLogger<LangfuseEnrichmentFilter>.Instance;
    }

    /// <inheritdoc />
    public Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        TryEnrich();
        return next(request, cancellationToken);
    }

    private void TryEnrich()
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        try
        {
            var context = _contextAccessor.Current;

            var userId = context.UserId ?? _options.AnonymousUserId;
            if (!string.IsNullOrEmpty(userId))
            {
                activity.SetTag(LangfuseTags.UserId, userId);
            }

            if (!string.IsNullOrEmpty(context.CorrelationId))
            {
                activity.SetTag(LangfuseTags.SessionId, context.CorrelationId);
            }

            if (!string.IsNullOrEmpty(context.AgentName))
            {
                activity.SetTag(LangfuseTags.TraceName, context.AgentName);
                activity.SetTag(LangfuseTags.MetadataPrefix + "agent_name", context.AgentName);
            }

            if (!string.IsNullOrEmpty(context.TenantId))
            {
                activity.SetTag(LangfuseTags.MetadataPrefix + "tenant_id", context.TenantId);
            }

            if (!string.IsNullOrEmpty(context.CorrelationId))
            {
                activity.SetTag(LangfuseTags.MetadataPrefix + "correlation_id", context.CorrelationId);
            }

            if (_options.StaticMetadata is { Count: > 0 })
            {
                foreach (var kv in _options.StaticMetadata)
                {
                    activity.SetTag(LangfuseTags.MetadataPrefix + kv.Key, kv.Value);
                }
            }

            if (_options.DefaultTags.Count > 0)
            {
                activity.SetTag(LangfuseTags.Tags, _options.DefaultTags);
            }
        }
        catch (Exception ex)
        {
            // Enrichment is best-effort — never break a turn over a failed tag set.
            _logger.LogWarning(ex, "Langfuse enrichment failed on activity {Activity}.", activity.OperationName);
        }
    }
}
