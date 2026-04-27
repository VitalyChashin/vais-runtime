// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Vais.Agents.Core;
using Vais.Agents.Gateways.OpenAiCompat.Models;

namespace Vais.Agents.Gateways.OpenAiCompat;

/// <summary>
/// Registers the OpenAI-compatible gateway endpoints onto an
/// <see cref="IEndpointRouteBuilder"/>. Call from the application host's
/// <c>Configure</c> phase: <c>app.MapOpenAiCompat()</c>.
/// </summary>
public static class OpenAiCompatEndpoints
{
    /// <summary>
    /// Maps <c>POST /v1/chat/completions</c> and <c>GET /v1/models</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapOpenAiCompat(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1");
        group.MapPost("/chat/completions", HandleChatCompletionAsync);
        group.MapGet("/models", HandleModelsAsync);
        return app;
    }

    private static async Task HandleChatCompletionAsync(
        HttpContext ctx,
        IInboundIdentityResolver identityResolver,
        IModelRouter modelRouter,
        IAgentContextSetter contextSetter,
        IEnumerable<LlmGatewayMiddleware> gatewayMiddleware,
        CancellationToken ct)
    {
        // 1. Deserialize request body
        ChatCompletionRequest? oaiRequest;
        try
        {
            oaiRequest = await ctx.Request.ReadFromJsonAsync<ChatCompletionRequest>(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest,
                "invalid_request_error", "Request body could not be parsed as JSON.", ct).ConfigureAwait(false);
            return;
        }

        if (oaiRequest is null || string.IsNullOrWhiteSpace(oaiRequest.Model))
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest,
                "invalid_request_error", "Field 'model' is required.", ct).ConfigureAwait(false);
            return;
        }

        // 2. Resolve identity
        AgentContext agentCtx;
        try
        {
            var bearer = ExtractBearer(ctx.Request.Headers.Authorization);
            agentCtx = await identityResolver.ResolveAsync(bearer, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status401Unauthorized,
                "invalid_api_key", ex.Message, ct).ConfigureAwait(false);
            return;
        }

        using var _ = contextSetter.Push(agentCtx);

        // 3. Resolve model route
        ModelRoute route;
        try
        {
            route = await modelRouter.ResolveAsync(oaiRequest.Model, ct).ConfigureAwait(false);
        }
        catch (ModelNotFoundException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status404NotFound,
                "model_not_found", ex.Message, ct).ConfigureAwait(false);
            return;
        }

        // 4. Translate request
        var request = OpenAiTranslator.ToCompletionRequest(oaiRequest);
        var middleware = gatewayMiddleware.ToArray();
        var completionId = $"chatcmpl-{Guid.NewGuid():N}";

        // 5. Execute chain
        try
        {
            if (oaiRequest.Stream == true)
            {
                if (route.Provider is not IStreamingCompletionProvider streamingProvider)
                {
                    await WriteErrorAsync(ctx.Response, StatusCodes.Status422UnprocessableEntity,
                        "streaming_not_supported",
                        $"Provider for model '{oaiRequest.Model}' does not support streaming.", ct).ConfigureAwait(false);
                    return;
                }

                var stream = LlmGatewayPipeline.StreamAsync(request, streamingProvider, middleware, ct);
                await OpenAiSseWriter.WriteStreamAsync(ctx.Response, completionId, oaiRequest.Model, stream, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var response = await LlmGatewayPipeline.InvokeAsync(request, route.Provider, middleware, ct)
                    .ConfigureAwait(false);
                var oaiResponse = OpenAiTranslator.ToChatCompletionResponse(response, oaiRequest.Model, completionId);
                await ctx.Response.WriteAsJsonAsync(oaiResponse, ct).ConfigureAwait(false);
            }
        }
        catch (AgentBudgetExceededException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status429TooManyRequests,
                "rate_limit_exceeded", ex.Message, ct).ConfigureAwait(false);
        }
        catch (AgentGuardrailDeniedException ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status400BadRequest,
                "content_policy_violation", ex.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ctx.Response, StatusCodes.Status500InternalServerError,
                "server_error", ex.Message, ct).ConfigureAwait(false);
        }
    }

    private static async Task HandleModelsAsync(
        HttpContext ctx,
        IModelRouter modelRouter,
        CancellationToken ct)
    {
        var aliases = await modelRouter.ListAliasesAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var models = aliases.Select(alias => new ModelObject
        {
            Id = alias,
            Created = now,
            OwnedBy = "vais"
        }).ToArray();

        var list = new ModelListResponse { Data = models };
        await ctx.Response.WriteAsJsonAsync(list, ct).ConfigureAwait(false);
    }

    private static string ExtractBearer(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
            return "";

        if (AuthenticationHeaderValue.TryParse(authHeader, out var parsed) &&
            string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return parsed.Parameter ?? "";
        }

        return "";
    }

    private static Task WriteErrorAsync(
        HttpResponse response,
        int statusCode,
        string type,
        string message,
        CancellationToken ct)
    {
        if (!response.HasStarted)
            response.StatusCode = statusCode;

        var error = new ChatErrorResponse
        {
            Error = new ChatError { Message = message, Type = type }
        };
        return response.WriteAsJsonAsync(error, ct);
    }
}
