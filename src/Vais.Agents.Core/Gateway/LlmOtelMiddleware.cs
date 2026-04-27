// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that emits an OpenTelemetry <see cref="Activity"/> span per LLM call on both
/// the non-streaming and streaming paths. Uses <see cref="AgenticDiagnostics.ActivitySource"/>.
/// </summary>
/// <remarks>
/// If no listener is attached to the activity source the span creation is a no-op and the middleware
/// adds zero overhead. The streaming activity lives for the duration of the full stream.
/// </remarks>
public sealed class LlmOtelMiddleware(IAgentContextAccessor contextAccessor) : LlmGatewayMiddleware
{
    /// <inheritdoc/>
    protected override async Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
    {
        using var activity = AgenticDiagnostics.ActivitySource.StartActivity("llm.completion");
        TagRequest(activity, request, contextAccessor.Current);
        try
        {
            var response = await next(request, cancellationToken).ConfigureAwait(false);
            TagResponse(activity, response);
            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => StreamWithActivity(request, next, cancellationToken);

    private async IAsyncEnumerable<CompletionUpdate> StreamWithActivity(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = AgenticDiagnostics.ActivitySource.StartActivity("llm.completion.stream");
        TagRequest(activity, request, contextAccessor.Current);
        await foreach (var update in next(request, cancellationToken).ConfigureAwait(false))
            yield return update;
        // Activity disposed here — span ends when the stream drains.
    }

    private static void TagRequest(Activity? activity, CompletionRequest request, AgentContext ctx)
    {
        if (activity is null) return;
        activity.SetTag(AgenticTags.AgentName, ctx.AgentName);
        activity.SetTag(AgenticTags.WorkspaceId, ctx.WorkspaceId);
        activity.SetTag("llm.turns", request.History.Count);
    }

    private static void TagResponse(Activity? activity, CompletionResponse response)
    {
        if (activity is null) return;
        activity.SetTag(AgenticTags.GenAiResponseModel, response.ModelId);
        activity.SetTag(AgenticTags.GenAiUsageInputTokens, response.PromptTokens);
        activity.SetTag(AgenticTags.GenAiUsageOutputTokens, response.CompletionTokens);
    }
}

public static partial class LlmGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LlmOtelMiddleware"/> as gateway middleware. Emits an OTel
    /// <see cref="Activity"/> span per LLM call on both paths using
    /// <see cref="AgenticDiagnostics.ActivitySource"/>.
    /// </summary>
    public static IServiceCollection AddLlmOtelMiddleware(
        this IServiceCollection services)
        => services.AddLlmGatewayMiddleware<LlmOtelMiddleware>();
}
