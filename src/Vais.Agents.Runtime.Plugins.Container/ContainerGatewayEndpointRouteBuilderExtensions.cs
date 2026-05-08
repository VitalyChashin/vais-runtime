// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Maps the internal container gateway endpoints:
/// <c>POST /v1/container-gateway/llm/complete</c> and
/// <c>POST /v1/container-gateway/tools/invoke</c>.
/// Call token validation is enforced on all routes.
/// </summary>
public static class ContainerGatewayEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Adds the container gateway callback endpoints. Call this from the runtime host's
    /// pipeline setup on the internal port (typically 5001).
    /// </summary>
    public static IEndpointRouteBuilder MapContainerGatewayEndpoints(
        this IEndpointRouteBuilder builder)
    {
        var callTokenService = builder.ServiceProvider.GetRequiredService<ICallTokenService>();

        var group = builder.MapGroup("/v1/container-gateway");

        group.AddEndpointFilter(async (ctx, next) =>
        {
            var runId = ctx.HttpContext.Request.Headers["X-Run-Id"].FirstOrDefault() ?? "";
            var agentId = ctx.HttpContext.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
            var authHeader = ctx.HttpContext.Request.Headers.Authorization.FirstOrDefault();
            var bearerToken = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? authHeader["Bearer ".Length..] : "";

            if (string.IsNullOrEmpty(bearerToken) || !callTokenService.Validate(bearerToken, runId, agentId))
                return Results.Unauthorized();

            return await next(ctx);
        });

        group.MapPost("llm/complete", HandleLlmCompleteAsync);
        group.MapPost("tools/invoke", HandleToolInvokeAsync);

        return builder;
    }

    private static async Task<IResult> HandleLlmCompleteAsync(
        HttpContext ctx,
        GatewayLlmCompleteRequest body,
        ICompletionProvider provider,
        CancellationToken ct)
    {
        if (ctx.Request.Headers.Accept.Any(
                h => h?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var history = body.Messages
            .Select(PluginMessageToChatTurn)
            .ToArray();

        var request = new CompletionRequest(history);
        var response = await provider.CompleteAsync(request, ct).ConfigureAwait(false);

        var replyMessage = new PluginMessage
        {
            Role = "assistant",
            Content = response.Text,
        };

        return Results.Ok(new GatewayLlmCompleteResponse
        {
            Message = replyMessage,
            Usage = new PluginUsageCounts
            {
                InputTokens = response.PromptTokens ?? 0,
                OutputTokens = response.CompletionTokens ?? 0,
            }
        });
    }

    private static async Task<IResult> HandleToolInvokeAsync(
        HttpContext ctx,
        GatewayToolInvokeRequest body,
        IToolCallDispatcher dispatcher,
        CancellationToken ct)
    {
        var agentCtx = new AgentContext(
            AgentName: ctx.Request.Headers["X-Agent-Id"].FirstOrDefault(),
            CorrelationId: ctx.Request.Headers["X-Correlation-Id"].FirstOrDefault())
        {
            RunId = ctx.Request.Headers["X-Run-Id"].FirstOrDefault(),
        };

        var toolRequest = new ToolCallRequest(body.ToolName, body.Arguments, body.ToolCallId);
        var outcome = await dispatcher.DispatchAsync(toolRequest, agentCtx, ct).ConfigureAwait(false);

        return Results.Ok(new GatewayToolInvokeResponse
        {
            ToolCallId = body.ToolCallId,
            Content = outcome.Result ?? outcome.Error ?? "",
            IsError = outcome.Error is not null,
        });
    }

    private static ChatTurn PluginMessageToChatTurn(PluginMessage msg)
    {
        var role = msg.Role switch
        {
            "system" => AgentChatRole.System,
            "user" => AgentChatRole.User,
            "assistant" => AgentChatRole.Assistant,
            "tool" => AgentChatRole.Tool,
            _ => AgentChatRole.User,
        };

        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        if (msg.ToolCalls is { Count: > 0 } tcs)
        {
            toolCalls = tcs.Select(tc => new ToolCallRequest(tc.Name, tc.Arguments, tc.Id)).ToArray();
        }

        return new ChatTurn(role, msg.Content ?? "", ToolCalls: toolCalls, ToolCallId: msg.ToolCallId);
    }
}
