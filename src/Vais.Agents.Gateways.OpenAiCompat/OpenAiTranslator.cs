// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Vais.Agents.Gateways.OpenAiCompat.Models;

namespace Vais.Agents.Gateways.OpenAiCompat;

/// <summary>
/// Translates between OpenAI wire-format types and Vais.Agents domain types.
/// All methods are static and allocation-minimised for the hot path.
/// </summary>
internal static class OpenAiTranslator
{
    /// <summary>
    /// Translates an inbound <see cref="ChatCompletionRequest"/> to a
    /// <see cref="CompletionRequest"/>. The first message with role "system"
    /// is extracted as <see cref="CompletionRequest.SystemPrompt"/>; all
    /// remaining messages become <see cref="CompletionRequest.History"/>.
    /// </summary>
    internal static CompletionRequest ToCompletionRequest(ChatCompletionRequest req)
    {
        string? systemPrompt = null;
        var history = new List<ChatTurn>(req.Messages.Count);

        foreach (var msg in req.Messages)
        {
            switch (msg.Role)
            {
                case "system" when systemPrompt is null:
                    systemPrompt = msg.Content;
                    break;

                case "assistant":
                    var toolCalls = msg.ToolCalls is { Count: > 0 }
                        ? msg.ToolCalls.Select(tc => new ToolCallRequest(
                            tc.Function.Name,
                            ParseArguments(tc.Function.Arguments),
                            tc.Id)).ToArray()
                        : null;
                    history.Add(new ChatTurn(
                        AgentChatRole.Assistant,
                        msg.Content ?? "",
                        ToolCalls: toolCalls));
                    break;

                case "tool":
                    history.Add(new ChatTurn(
                        AgentChatRole.Tool,
                        msg.Content ?? "",
                        ToolCallId: msg.ToolCallId));
                    break;

                default:
                    // "user" and any unrecognised roles treated as user turns
                    history.Add(new ChatTurn(AgentChatRole.User, msg.Content ?? ""));
                    break;
            }
        }

        IReadOnlyList<ITool>? tools = req.Tools is { Count: > 0 }
            ? req.Tools.Select(t => (ITool)new ChatToolAdapter(t)).ToArray()
            : null;

        return new CompletionRequest(
            history,
            SystemPrompt: systemPrompt,
            Temperature: req.Temperature.HasValue ? (float)req.Temperature.Value : null,
            MaxTokens: req.MaxTokens,
            Tools: tools);
    }

    /// <summary>
    /// Translates a <see cref="CompletionResponse"/> to an OpenAI-format
    /// <see cref="ChatCompletionResponse"/>.
    /// </summary>
    internal static ChatCompletionResponse ToChatCompletionResponse(
        CompletionResponse response,
        string model,
        string completionId)
    {
        var hasToolCalls = response.ToolCalls is { Count: > 0 };
        var finishReason = hasToolCalls ? "tool_calls" : "stop";

        IReadOnlyList<ChatToolCall>? oaiToolCalls = hasToolCalls
            ? response.ToolCalls!.Select((tc, idx) => new ChatToolCall
            {
                Id = tc.CallId,
                Function = new ChatFunctionCallResult
                {
                    Name = tc.ToolName,
                    Arguments = tc.Arguments.GetRawText()
                }
            }).ToArray()
            : null;

        var message = new ChatMessage
        {
            Role = "assistant",
            Content = response.Text.Length > 0 ? response.Text : null,
            ToolCalls = oaiToolCalls
        };

        ChatUsage? usage = null;
        if (response.PromptTokens.HasValue || response.CompletionTokens.HasValue)
        {
            var prompt = response.PromptTokens ?? 0;
            var completion = response.CompletionTokens ?? 0;
            usage = new ChatUsage
            {
                PromptTokens = prompt,
                CompletionTokens = completion,
                TotalTokens = prompt + completion
            };
        }

        return new ChatCompletionResponse
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices =
            [
                new ChatCompletionChoice
                {
                    Index = 0,
                    Message = message,
                    FinishReason = finishReason
                }
            ],
            Usage = usage
        };
    }

    /// <summary>Converts a <see cref="CompletionUpdate"/> to a streaming chunk.</summary>
    internal static ChatCompletionChunk ToChunk(
        CompletionUpdate update,
        string completionId,
        string model,
        string? finishReason)
    {
        IReadOnlyList<ChatToolCallDelta>? toolCallDeltas = null;
        if (update.ToolCalls is { Count: > 0 })
        {
            toolCallDeltas = update.ToolCalls.Select((tc, idx) => new ChatToolCallDelta
            {
                Index = idx,
                Id = tc.CallId,
                Type = "function",
                Function = new ChatFunctionDelta
                {
                    Name = tc.ToolName,
                    Arguments = tc.Arguments.GetRawText()
                }
            }).ToArray();
        }

        var delta = new ChatDelta
        {
            Content = update.TextDelta.Length > 0 ? update.TextDelta : null,
            ToolCalls = toolCallDeltas
        };

        return new ChatCompletionChunk
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices =
            [
                new ChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = delta,
                    FinishReason = finishReason
                }
            ]
        };
    }

    private static JsonElement ParseArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return JsonDocument.Parse("{}").RootElement;
        try
        {
            return JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
    }

    private sealed class ChatToolAdapter : ITool
    {
        private readonly ChatTool _tool;

        internal ChatToolAdapter(ChatTool tool) => _tool = tool;

        public string Name => _tool.Function.Name;
        public string Description => _tool.Function.Description ?? "";
        public JsonElement ParametersSchema => _tool.Function.Parameters;

        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "Tool execution is performed client-side in the OpenAI-compatible gateway transport.");
    }
}
