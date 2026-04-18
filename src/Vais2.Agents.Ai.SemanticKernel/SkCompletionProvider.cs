// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using SkChatMessageContent = Microsoft.SemanticKernel.ChatMessageContent;

namespace Vais2.Agents.Ai.SemanticKernel;

/// <summary>
/// Semantic Kernel-backed <see cref="ICompletionProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses SK's native <see cref="IChatCompletionService"/> path. We deliberately do
/// not shortcut through <c>Microsoft.Extensions.AI.IChatClient</c> here — the point
/// of an adapter is to exercise the host stack's real machinery, so the abstraction
/// gets tested against SK-specific quirks (prompt settings, usage metadata shape).
/// </para>
/// <para>
/// <b>Tool-call behavior (v0.4+):</b> the non-streaming path uses
/// <see cref="FunctionChoiceBehavior.None"/> and surfaces model-requested tool calls
/// on <see cref="CompletionResponse.ToolCalls"/> instead of auto-invoking them in
/// SK. <c>StatefulAiAgent</c>'s outer loop takes over dispatch via
/// <c>IToolCallDispatcher</c>. The streaming path keeps
/// <see cref="FunctionChoiceBehavior.Auto"/> — tool-using streaming is not supported
/// through <c>StatefulAiAgent.StreamAsync</c> in v0.4; consumers calling
/// <see cref="StreamAsync"/> directly still see SK's built-in auto-invocation.
/// </para>
/// </remarks>
public sealed class SkCompletionProvider : ICompletionProvider, IStreamingCompletionProvider
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatService;
    private readonly string _modelId;

    /// <summary>
    /// Create a provider bound to an SK <see cref="Kernel"/>. The kernel must have
    /// a chat-completion service registered (for example via
    /// <c>AddOpenAIChatCompletion</c>).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="kernel"/> is null.</exception>
    public SkCompletionProvider(Kernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _kernel = kernel;
        _chatService = kernel.GetRequiredService<IChatCompletionService>();
        _modelId = _chatService.Attributes.TryGetValue("ModelId", out var m)
            ? m?.ToString() ?? "unknown"
            : "unknown";
    }

    /// <inheritdoc />
    public string ProviderName => "SemanticKernel";

    /// <inheritdoc />
    public async Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var history = BuildChatHistory(request);
        var settings = new OpenAIPromptExecutionSettings
        {
            MaxTokens = request.MaxTokens ?? 1024,
            Temperature = request.Temperature ?? 0.2,
        };

        // When the request carries tools, clone the kernel and attach them as a
        // plugin. Cloning keeps the per-turn mutation local so concurrent calls
        // don't step on each other's plugin set. FunctionChoiceBehavior.None tells
        // SK to advertise the tools to the model but NOT auto-invoke them — tool
        // calls surface back on the returned message for StatefulAiAgent's loop
        // to dispatch.
        var kernel = _kernel;
        if (request.Tools is { Count: > 0 } tools)
        {
            kernel = _kernel.Clone();
            kernel.Plugins.Add(SkToolBinder.BuildPlugin(tools));
            settings.FunctionChoiceBehavior = FunctionChoiceBehavior.None();
        }

        var result = await _chatService
            .GetChatMessageContentAsync(history, settings, kernel, cancellationToken)
            .ConfigureAwait(false);

        var text = result.Content ?? string.Empty;
        var (promptTokens, completionTokens) = ExtractUsage(result.Metadata);
        var toolCalls = ExtractToolCalls(result);

        return new CompletionResponse(text, _modelId, promptTokens, completionTokens, toolCalls);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var history = BuildChatHistory(request);
        var settings = new OpenAIPromptExecutionSettings
        {
            MaxTokens = request.MaxTokens ?? 1024,
            Temperature = request.Temperature ?? 0.2,
        };

        var kernel = _kernel;
        if (request.Tools is { Count: > 0 } tools)
        {
            kernel = _kernel.Clone();
            kernel.Plugins.Add(SkToolBinder.BuildPlugin(tools));
            // Auto-invoke on streaming is legacy v0.3 behaviour retained for now —
            // tool-using streaming via StatefulAiAgent.StreamAsync is not supported
            // in v0.4 (no loop there). A future milestone lands streaming-tool
            // accumulation properly.
            settings.FunctionChoiceBehavior = FunctionChoiceBehavior.Auto();
        }

        // SK's streaming surface emits one StreamingChatMessageContent per chunk. We map each
        // to a CompletionUpdate — text goes in TextDelta; ModelId is piped through every update
        // (cheap) so a consumer that only inspects the final update still sees it. Token usage
        // is typically only on the last streamed item's Metadata dictionary.
        await foreach (var chunk in _chatService
            .GetStreamingChatMessageContentsAsync(history, settings, kernel, cancellationToken)
            .ConfigureAwait(false))
        {
            var delta = chunk.Content ?? string.Empty;
            var (promptTokens, completionTokens) = ExtractUsage(chunk.Metadata);
            yield return new CompletionUpdate(delta, _modelId, promptTokens, completionTokens);
        }
    }

    private static ChatHistory BuildChatHistory(CompletionRequest request)
    {
        var history = new ChatHistory();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            history.AddSystemMessage(request.SystemPrompt);
        }

        foreach (var turn in request.History)
        {
            switch (turn.Role)
            {
                case AgentChatRole.System:
                    history.AddSystemMessage(turn.Text);
                    break;
                case AgentChatRole.User:
                    history.AddUserMessage(turn.Text);
                    break;
                case AgentChatRole.Assistant:
                    if (turn.ToolCalls is { Count: > 0 } calls)
                    {
                        var msg = new SkChatMessageContent(AuthorRole.Assistant, turn.Text);
                        foreach (var call in calls)
                        {
                            msg.Items.Add(new FunctionCallContent(
                                functionName: call.ToolName,
                                pluginName: null,
                                id: call.CallId,
                                arguments: JsonElementToKernelArguments(call.Arguments)));
                        }
                        history.Add(msg);
                    }
                    else
                    {
                        history.AddAssistantMessage(turn.Text);
                    }
                    break;
                case AgentChatRole.Tool:
                    // SK tool-role message: carries a FunctionResultContent item. The
                    // OpenAI wire only requires the call id + result; function name is
                    // informational here so we leave it empty.
                    var toolMsg = new SkChatMessageContent(AuthorRole.Tool, turn.Text);
                    toolMsg.Items.Add(new FunctionResultContent(
                        functionName: string.Empty,
                        pluginName: null,
                        callId: turn.ToolCallId ?? string.Empty,
                        result: turn.Text));
                    history.Add(toolMsg);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(request),
                        turn.Role,
                        $"Unsupported chat role: {turn.Role}");
            }
        }

        return history;
    }

    private static (int? Prompt, int? Completion) ExtractUsage(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null)
        {
            return (null, null);
        }

        if (metadata.TryGetValue("Usage", out var usageObj) && usageObj is ChatTokenUsage usage)
        {
            return (usage.InputTokenCount, usage.OutputTokenCount);
        }

        return (null, null);
    }

    private static IReadOnlyList<ToolCallRequest>? ExtractToolCalls(SkChatMessageContent result)
    {
        var calls = FunctionCallContent.GetFunctionCalls(result).ToList();
        if (calls.Count == 0)
        {
            return null;
        }

        var requests = new List<ToolCallRequest>(calls.Count);
        foreach (var call in calls)
        {
            requests.Add(new ToolCallRequest(
                ToolName: call.FunctionName,
                Arguments: SerializeKernelArguments(call.Arguments),
                CallId: call.Id ?? string.Empty));
        }
        return requests;
    }

    private static JsonElement SerializeKernelArguments(KernelArguments? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
        var dict = new Dictionary<string, object?>(arguments.Count);
        foreach (var kv in arguments)
        {
            dict[kv.Key] = kv.Value;
        }
        return JsonSerializer.SerializeToElement(dict);
    }

    private static KernelArguments? JsonElementToKernelArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var result = new KernelArguments();
        foreach (var prop in arguments.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object?)l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.ToString(),
            };
        }
        return result;
    }
}
