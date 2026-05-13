// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;

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
        group.MapPost("chat/completions", HandleChatCompletionsAsync);
        group.MapPost("tools/invoke", HandleToolInvokeAsync);
        group.MapGet("tools/list", HandleToolsListAsync);

        return builder;
    }

    private static async Task<IResult> HandleLlmCompleteAsync(
        HttpContext ctx,
        GatewayLlmCompleteRequest body,
        ICompletionProviderPool pool,
        CancellationToken ct)
    {
        if (ctx.Request.Headers.Accept.Any(
                h => h?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var modelId = string.IsNullOrEmpty(body.ModelId) ? "gpt-4o-mini" : body.ModelId;
        var provider = await pool.GetAsync(
            new ModelSpec("openai", modelId, ApiKeyRef: "secret://env/OPENAI_API_KEY"), ct)
            .ConfigureAwait(false);

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

    private static async Task<IResult> HandleChatCompletionsAsync(
        HttpContext ctx,
        OpenAiChatRequest body,
        ICompletionProviderPool pool,
        CancellationToken ct)
    {
        if (body.Stream == true)
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

        var provider = await pool.GetAsync(
            new ModelSpec("openai", body.Model, ApiKeyRef: "secret://env/OPENAI_API_KEY"), ct)
            .ConfigureAwait(false);

        var history = body.Messages
            .Select(m => new ChatTurn(
                m.Role switch
                {
                    "system"    => AgentChatRole.System,
                    "assistant" => AgentChatRole.Assistant,
                    "tool"      => AgentChatRole.Tool,
                    _           => AgentChatRole.User,
                },
                m.Content ?? ""))
            .ToArray();

        var request = new CompletionRequest(history, Temperature: body.Temperature, MaxTokens: body.MaxTokens);
        var response = await provider.CompleteAsync(request, ct).ConfigureAwait(false);

        return Results.Ok(new OpenAiChatResponse
        {
            Id      = $"chatcmpl-{Guid.NewGuid():N}",
            Object  = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model   = body.Model,
            Choices = [new OpenAiChatChoice
            {
                Index        = 0,
                Message      = new OpenAiChatMessage { Role = "assistant", Content = response.Text },
                FinishReason = "stop",
            }],
            Usage = new OpenAiUsage
            {
                PromptTokens     = response.PromptTokens     ?? 0,
                CompletionTokens = response.CompletionTokens ?? 0,
                TotalTokens      = (response.PromptTokens ?? 0) + (response.CompletionTokens ?? 0),
            },
        });
    }

    private static async Task<IResult> HandleToolInvokeAsync(
        GatewayToolInvokeRequest body,
        IMcpServerRegistry? registry,
        IEnumerable<INamedToolSourceProvider> providers,
        CancellationToken ct)
    {
        if (registry is null)
            return Results.Ok(new GatewayToolInvokeResponse
            {
                ToolCallId = body.ToolCallId,
                Content = $"Tool '{body.ToolName}' not found: no tool registry.",
                IsError = true,
            });

        ITool? tool = null;
        await foreach (var server in registry.ListAsync(ct: ct).ConfigureAwait(false))
        {
            IToolSource? source = null;
            foreach (var provider in providers)
            {
                source = provider.GetByName(server.Id);
                if (source is not null) break;
            }
            if (source is null) continue;

            await foreach (var t in source.DiscoverAsync(ct).ConfigureAwait(false))
            {
                if (string.Equals(t.Name, body.ToolName, StringComparison.OrdinalIgnoreCase))
                {
                    tool = t;
                    break;
                }
            }
            if (tool is not null) break;
        }

        if (tool is null)
            return Results.Ok(new GatewayToolInvokeResponse
            {
                ToolCallId = body.ToolCallId,
                Content = $"Tool '{body.ToolName}' not found.",
                IsError = true,
            });

        try
        {
            var result = await tool.InvokeAsync(body.Arguments, ct).ConfigureAwait(false);
            return Results.Ok(new GatewayToolInvokeResponse
            {
                ToolCallId = body.ToolCallId,
                Content = result,
                IsError = false,
            });
        }
        catch (Exception ex)
        {
            return Results.Ok(new GatewayToolInvokeResponse
            {
                ToolCallId = body.ToolCallId,
                Content = $"Tool '{body.ToolName}' failed: {ex.Message}",
                IsError = true,
            });
        }
    }

    private static async Task<IResult> HandleToolsListAsync(
        IMcpServerRegistry? registry,
        IEnumerable<INamedToolSourceProvider> providers,
        CancellationToken ct)
    {
        if (registry is null)
            return Results.Ok(new GatewayToolListResponse());

        var tools = new List<GatewayToolInfo>();
        await foreach (var server in registry.ListAsync(ct: ct).ConfigureAwait(false))
        {
            IToolSource? source = null;
            foreach (var provider in providers)
            {
                source = provider.GetByName(server.Id);
                if (source is not null) break;
            }
            if (source is null) continue;

            await foreach (var tool in source.DiscoverAsync(ct).ConfigureAwait(false))
            {
                tools.Add(new GatewayToolInfo
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    ParametersSchema = tool.ParametersSchema,
                });
            }
        }

        return Results.Ok(new GatewayToolListResponse { Tools = tools });
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
