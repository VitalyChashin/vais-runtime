// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.AI;

namespace Vais.Agents.ParityTests;

/// <summary>
/// Test double <see cref="IChatClient"/>: plays back a pre-scripted sequence of
/// responses. Used by the MAF-side parity scenario to simulate a tool-calling
/// conversation without touching the network.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses;

    public ScriptedChatClient(IEnumerable<ChatResponse> scripted)
    {
        _responses = new Queue<ChatResponse>(scripted);
    }

    public List<IList<ChatMessage>> Invocations { get; } = new();

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add(messages.ToList());
        if (!_responses.TryDequeue(out var next))
        {
            throw new InvalidOperationException("ScriptedChatClient ran out of scripted responses.");
        }
        return Task.FromResult(next);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata(providerName: "scripted", defaultModelId: "scripted-model");
        }
        return null;
    }

    public void Dispose() { }
}
