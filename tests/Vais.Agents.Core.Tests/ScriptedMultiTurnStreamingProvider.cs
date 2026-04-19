// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Test double that cycles through a queue of pre-scripted streamed-turn scripts.
/// Each call to <see cref="StreamAsync"/> dequeues the next script and yields its
/// updates in order. Lets a single test drive the tool-using streaming outer
/// loop across multiple "turns" — one tool-call round plus the final answer,
/// or more tool-call rounds back-to-back.
/// </summary>
internal sealed class ScriptedMultiTurnStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
{
    private readonly Queue<IEnumerable<CompletionUpdate>> _scripts;

    public ScriptedMultiTurnStreamingProvider(params IEnumerable<CompletionUpdate>[] scriptsPerCall)
    {
        _scripts = new Queue<IEnumerable<CompletionUpdate>>(scriptsPerCall);
    }

    public List<CompletionRequest> RequestsSeen { get; } = new();

    public int CallCount => RequestsSeen.Count;

    public string ProviderName => "ScriptedMultiTurnStreaming";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This provider supports streaming only.");

#pragma warning disable CS1998 // Async method lacks 'await' — iterator body is synchronous by design.
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequestsSeen.Add(request);
        if (!_scripts.TryDequeue(out var script))
        {
            throw new InvalidOperationException("ScriptedMultiTurnStreamingProvider ran out of scripts.");
        }

        foreach (var update in script)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }
#pragma warning restore CS1998
}
