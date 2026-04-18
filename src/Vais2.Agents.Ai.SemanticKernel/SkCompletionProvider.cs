// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;

namespace Vais2.Agents.Ai.SemanticKernel;

/// <summary>
/// Semantic Kernel-backed <see cref="ICompletionProvider"/>.
/// </summary>
/// <remarks>
/// Uses SK's native <see cref="IChatCompletionService"/> path. We deliberately do
/// not shortcut through <c>Microsoft.Extensions.AI.IChatClient</c> here — the point
/// of an adapter is to exercise the host stack's real machinery, so the abstraction
/// gets tested against SK-specific quirks (prompt settings, usage metadata shape).
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
        // plugin. Cloning keeps the per-turn mutation local — multiple concurrent
        // calls don't step on each other's plugin set. Auto-invocation is enabled
        // so SK handles tool calls inline and returns the final assistant text.
        var kernel = _kernel;
        if (request.Tools is { Count: > 0 } tools)
        {
            kernel = _kernel.Clone();
            kernel.Plugins.Add(SkToolBinder.BuildPlugin(tools));
            settings.FunctionChoiceBehavior = FunctionChoiceBehavior.Auto();
        }

        var result = await _chatService
            .GetChatMessageContentAsync(history, settings, kernel, cancellationToken)
            .ConfigureAwait(false);

        var text = result.Content ?? string.Empty;
        var (promptTokens, completionTokens) = ExtractUsage(result.Metadata);

        return new CompletionResponse(text, _modelId, promptTokens, completionTokens);
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
                case ChatRole.System:
                    history.AddSystemMessage(turn.Text);
                    break;
                case ChatRole.User:
                    history.AddUserMessage(turn.Text);
                    break;
                case ChatRole.Assistant:
                    history.AddAssistantMessage(turn.Text);
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
}
