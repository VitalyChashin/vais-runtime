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
        IEnumerable<LlmGatewayMiddleware> gatewayMiddleware,
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

        var runId   = ctx.Request.Headers["X-Run-Id"].FirstOrDefault()   ?? "";
        var agentId = ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
        using var _ = ctx.RequestServices.GetService<IAgentContextSetter>()
            ?.Push(new AgentContext(AgentName: agentId) { RunId = runId });

        var request = new CompletionRequest(history);
        var response = await LlmGatewayPipeline.InvokeAsync(request, provider, gatewayMiddleware, ct)
            .ConfigureAwait(false);

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
        IEnumerable<LlmGatewayMiddleware> gatewayMiddleware,
        CancellationToken ct)
    {
        var provider = await pool.GetAsync(
            new ModelSpec("openai", body.Model, ApiKeyRef: "secret://env/OPENAI_API_KEY"), ct)
            .ConfigureAwait(false);

        var history = body.Messages
            .Select(OpenAiMessageToChatTurn)
            .ToArray();

        ResponseFormatSpec? responseFormat = null;
        if (body.ResponseFormat is { Type: "json_schema", JsonSchema: { } js })
            responseFormat = new ResponseFormatSpec(js.Schema, js.Name, js.Strict ?? true);

        var request = new CompletionRequest(
            history,
            Temperature:    body.Temperature,
            MaxTokens:      body.MaxTokens,
            ResponseFormat: responseFormat);

        var runId   = ctx.Request.Headers["X-Run-Id"].FirstOrDefault()   ?? "";
        var agentId = ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
        using var _ = ctx.RequestServices.GetService<IAgentContextSetter>()
            ?.Push(new AgentContext(AgentName: agentId) { RunId = runId });

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";

        if (body.Stream == true)
        {
            if (provider is not IStreamingCompletionProvider streamingProvider)
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);

            var stream = LlmGatewayPipeline.StreamAsync(request, streamingProvider, gatewayMiddleware, ct);
            await ContainerGatewaySseWriter.WriteAsync(ctx.Response, completionId, body.Model, stream, ct)
                .ConfigureAwait(false);
            return Results.Empty;
        }

        var response = await LlmGatewayPipeline.InvokeAsync(request, provider, gatewayMiddleware, ct)
            .ConfigureAwait(false);

        return Results.Ok(new OpenAiChatResponse
        {
            Id      = completionId,
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
        HttpContext ctx,
        GatewayToolInvokeRequest body,
        IMcpServerRegistry? registry,
        IEnumerable<INamedToolSourceProvider> providers,
        IEnumerable<IToolGuardrail> guardrails,
        IEnumerable<ToolGatewayMiddleware> toolMiddleware,
        CancellationToken ct)
    {
        if (registry is null)
            return ToolNotFound(body.ToolCallId, body.ToolName, "no tool registry");

        var tool = await FindToolAsync(body.ToolName, registry, providers, ct).ConfigureAwait(false);
        if (tool is null)
            return ToolNotFound(body.ToolCallId, body.ToolName, "not found");

        var runId   = ctx.Request.Headers["X-Run-Id"].FirstOrDefault()   ?? "";
        var agentId = ctx.Request.Headers["X-Agent-Id"].FirstOrDefault() ?? "";
        var agentCtx = new AgentContext(AgentName: agentId) { RunId = runId };
        using var _ = ctx.RequestServices.GetService<IAgentContextSetter>()?.Push(agentCtx);

        // DefaultToolCallDispatcher gives us: IToolGuardrail Before/After hooks,
        // IAgentJournal append (when RunId is set), IAgentEventBus ToolCallStarted/Completed,
        // ToolGatewayMiddleware chain — same path C# agents use via StatefulAiAgent.
        var dispatcher = new DefaultToolCallDispatcher(
            toolRegistry:      new SingleToolRegistry(tool),
            toolGuardrails:    guardrails.ToArray(),
            eventBus:          ctx.RequestServices.GetService<IAgentEventBus>(),
            journal:           ctx.RequestServices.GetService<IAgentJournal>(),
            gatewayMiddleware: toolMiddleware);

        ToolCallOutcome outcome;
        try
        {
            outcome = await dispatcher.DispatchAsync(
                new ToolCallRequest(body.ToolName, body.Arguments, body.ToolCallId),
                agentCtx,
                ct).ConfigureAwait(false);
        }
        catch (AgentGuardrailDeniedException ex)
        {
            return Results.Ok(new GatewayToolInvokeResponse
            {
                ToolCallId = body.ToolCallId,
                Content    = $"Tool '{body.ToolName}' denied by guardrail: {ex.Message}",
                IsError    = true,
            });
        }

        return Results.Ok(new GatewayToolInvokeResponse
        {
            ToolCallId = body.ToolCallId,
            Content    = outcome.Result ?? outcome.Error ?? "",
            IsError    = outcome.Error is not null,
        });
    }

    private static IResult ToolNotFound(string toolCallId, string toolName, string reason) =>
        Results.Ok(new GatewayToolInvokeResponse
        {
            ToolCallId = toolCallId,
            Content    = $"Tool '{toolName}' not found: {reason}.",
            IsError    = true,
        });

    private static async Task<ITool?> FindToolAsync(
        string toolName,
        IMcpServerRegistry registry,
        IEnumerable<INamedToolSourceProvider> providers,
        CancellationToken ct)
    {
        await foreach (var server in registry.ListAsync(ct: ct).ConfigureAwait(false))
        {
            IToolSource? source = null;
            foreach (var p in providers)
            {
                source = p.GetByName(server.Id);
                if (source is not null) break;
            }
            if (source is null) continue;

            await foreach (var t in source.DiscoverAsync(ct).ConfigureAwait(false))
                if (string.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase))
                    return t;
        }
        return null;
    }

    private sealed class SingleToolRegistry(ITool tool) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = [tool];

        public ITool? GetByName(string name)
            => string.Equals(name, tool.Name, StringComparison.OrdinalIgnoreCase) ? tool : null;
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

    private static ChatTurn OpenAiMessageToChatTurn(OpenAiChatMessage msg)
    {
        var role = msg.Role switch
        {
            "system"    => AgentChatRole.System,
            "assistant" => AgentChatRole.Assistant,
            "tool"      => AgentChatRole.Tool,
            _           => AgentChatRole.User,
        };

        IReadOnlyList<ToolCallRequest>? toolCalls = null;
        if (msg.ToolCalls is { Count: > 0 } tcs)
        {
            toolCalls = tcs.Select(tc =>
            {
                // OpenAI sends function.arguments as a JSON-encoded string; parse so downstream
                // providers receive structured JsonElement values rather than an opaque string.
                JsonElement args;
                if (string.IsNullOrEmpty(tc.Function.Arguments))
                {
                    using var empty = JsonDocument.Parse("{}");
                    args = empty.RootElement.Clone();
                }
                else
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(tc.Function.Arguments);
                        args = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        using var fallback = JsonDocument.Parse("{}");
                        args = fallback.RootElement.Clone();
                    }
                }
                return new ToolCallRequest(tc.Function.Name, args, tc.Id);
            }).ToArray();
        }

        return new ChatTurn(role, msg.Content ?? "", ToolCalls: toolCalls, ToolCallId: msg.ToolCallId);
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
