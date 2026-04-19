// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Vais.Agents.ParityTests;

/// <summary>
/// Test double <see cref="IChatCompletionService"/> for streaming. Two constructors:
/// a simple string-list form that yields text-only <see cref="StreamingChatMessageContent"/>s,
/// and a <see cref="Chunk"/>-list form that can also emit
/// <see cref="StreamingFunctionCallUpdateContent"/> items — needed to exercise the
/// adapter's terminal-tool-call emission on streamed turns. Non-streaming path is not
/// supported — use <see cref="ScriptedChatCompletionService"/> if you need both.
/// </summary>
internal sealed class ScriptedStreamingChatCompletionService : IChatCompletionService
{
    private readonly IReadOnlyList<Chunk> _chunks;

    public ScriptedStreamingChatCompletionService(IEnumerable<string> textChunks)
        : this(textChunks.Select(t => new Chunk(Text: t)).ToArray())
    {
    }

    public ScriptedStreamingChatCompletionService(IEnumerable<Chunk> chunks)
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

            var msg = new StreamingChatMessageContent(AuthorRole.Assistant, chunk.Text ?? string.Empty);
            if (chunk.FunctionCallUpdate is { } fcu)
            {
                // SK's FunctionCallContentBuilder keys fragments by CallId — one fragment
                // carrying the full arg JSON rebuilds to a complete FunctionCallContent.
                msg.Items.Add(new StreamingFunctionCallUpdateContent(
                    callId: fcu.CallId,
                    name: fcu.FunctionName,
                    arguments: fcu.ArgumentsFragment,
                    functionCallIndex: 0));
            }
            yield return msg;
        }
    }
#pragma warning restore CS1998

    /// <summary>One element of a scripted stream — either pure text or a function-call update (or both).</summary>
    public sealed record Chunk(string? Text = null, StreamingFunctionCallUpdate? FunctionCallUpdate = null);
}

/// <summary>Minimal value type describing a scripted tool-call fragment for
/// <see cref="ScriptedStreamingChatCompletionService.Chunk"/>.</summary>
internal sealed record StreamingFunctionCallUpdate(string CallId, string FunctionName, string ArgumentsFragment);
