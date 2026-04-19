// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Vais.Agents.ParityTests;

/// <summary>
/// Test double <see cref="IChatClient"/> for streaming. Two constructors:
/// a simple string-list form that yields text-only <see cref="ChatResponseUpdate"/>s
/// for single-turn tests, and a per-call scripts form that returns a different
/// sequence of updates on each <see cref="GetStreamingResponseAsync"/> call —
/// used by the streaming tool-call outer-loop tests where each "streamed turn"
/// needs its own update sequence. Non-streaming path is not supported — use
/// <see cref="ScriptedChatClient"/> if you need both.
/// </summary>
internal sealed class ScriptedStreamingChatClient : IChatClient
{
    private readonly Queue<IEnumerable<ChatResponseUpdate>> _scripts;

    public ScriptedStreamingChatClient(IEnumerable<string> chunks)
        : this(new[] { chunks.Select(c => new ChatResponseUpdate(MeaiChatRole.Assistant, c)) })
    {
    }

    public ScriptedStreamingChatClient(IEnumerable<IEnumerable<ChatResponseUpdate>> scriptsPerCall)
    {
        _scripts = new Queue<IEnumerable<ChatResponseUpdate>>(scriptsPerCall);
    }

    public int CallCount { get; private set; }

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
        CallCount++;
        if (!_scripts.TryDequeue(out var script))
        {
            throw new InvalidOperationException("ScriptedStreamingChatClient ran out of scripted call scripts.");
        }
        foreach (var update in script)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
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
