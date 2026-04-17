// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
public sealed class SkCompletionProvider : ICompletionProvider
{
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

        var result = await _chatService
            .GetChatMessageContentAsync(history, settings, kernel: null, cancellationToken)
            .ConfigureAwait(false);

        var text = result.Content ?? string.Empty;
        var (promptTokens, completionTokens) = ExtractUsage(result.Metadata);

        return new CompletionResponse(text, _modelId, promptTokens, completionTokens);
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
