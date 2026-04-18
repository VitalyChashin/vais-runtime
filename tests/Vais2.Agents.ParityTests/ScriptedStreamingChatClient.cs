// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Vais2.Agents.ParityTests;

/// <summary>
/// Test double <see cref="IChatClient"/> for streaming: plays back a pre-scripted
/// sequence of text chunks as <see cref="ChatResponseUpdate"/> items. Non-streaming
/// path is not supported — use <see cref="ScriptedChatClient"/> if you need both.
/// </summary>
internal sealed class ScriptedStreamingChatClient : IChatClient
{
    private readonly IReadOnlyList<string> _chunks;

    public ScriptedStreamingChatClient(IEnumerable<string> chunks)
    {
        _chunks = chunks.ToArray();
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Non-streaming path is not supported by this test double.");

#pragma warning disable CS1998 // Async method lacks 'await' — iterator body is synchronous by design.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in _chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(MeaiChatRole.Assistant, chunk);
        }
    }
#pragma warning restore CS1998

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(providerName: "scripted-streaming", defaultModelId: "scripted-streaming-model");
        }
        return null;
    }

    public void Dispose() { }
}
