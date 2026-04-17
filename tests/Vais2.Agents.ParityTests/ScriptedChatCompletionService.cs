// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Vais2.Agents.ParityTests;

/// <summary>
/// Test double <see cref="IChatCompletionService"/>: plays back a pre-scripted
/// sequence of <see cref="ChatMessageContent"/> responses. Used by the SK-side
/// parity scenario to simulate a tool-calling conversation without touching a model.
/// </summary>
internal sealed class ScriptedChatCompletionService : IChatCompletionService
{
    private readonly Queue<ChatMessageContent> _responses;

    public ScriptedChatCompletionService(IEnumerable<ChatMessageContent> scripted)
    {
        _responses = new Queue<ChatMessageContent>(scripted);
    }

    public List<ChatHistory> Invocations { get; } = new();

    public IReadOnlyDictionary<string, object?> Attributes { get; } = new Dictionary<string, object?>
    {
        ["ModelId"] = "scripted-model",
    };

    public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add(chatHistory);
        if (!_responses.TryDequeue(out var next))
        {
            throw new InvalidOperationException("ScriptedChatCompletionService ran out of scripted responses.");
        }
        return Task.FromResult<IReadOnlyList<ChatMessageContent>>(new[] { next });
    }

    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotSupportedException();
#pragma warning disable CS0162 // unreachable, keeps compiler happy about yield
        yield break;
#pragma warning restore CS0162
    }
}
