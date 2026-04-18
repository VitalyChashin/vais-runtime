// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Vais2.Agents.ParityTests;

/// <summary>
/// Test double <see cref="IChatCompletionService"/> for streaming: plays back a
/// pre-scripted sequence of text chunks as <see cref="StreamingChatMessageContent"/>
/// items. Non-streaming path is not supported — use
/// <see cref="ScriptedChatCompletionService"/> if you need both.
/// </summary>
internal sealed class ScriptedStreamingChatCompletionService : IChatCompletionService
{
    private readonly IReadOnlyList<string> _chunks;

    public ScriptedStreamingChatCompletionService(IEnumerable<string> chunks)
    {
        _chunks = chunks.ToArray();
    }

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>
    {
        ["ModelId"] = "scripted-streaming-model",
    };

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Non-streaming path is not supported by this test double.");

#pragma warning disable CS1998 // Async method lacks 'await' — iterator body is synchronous by design.
    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in _chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new StreamingChatMessageContent(AuthorRole.Assistant, chunk);
        }
    }
#pragma warning restore CS1998
}
