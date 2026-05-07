// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// StreamingFilterTypingIndicator — IStreamingAgentFilter with all three hooks.
//
// Run: dotnet run --project samples/StreamingFilterTypingIndicator
// Env: none (deterministic, no API key)
// Docs: docs/concepts/streaming-filters.md
//
// A TypingIndicatorFilter:
//   • InvokeAsync          — around-provider; prints a banner before and after the inner stream
//   • OnStreamDeltaAsync   — per-delta transform hook; counts deltas and marks each with '·'
//   • OnStreamCompleteAsync — end-of-stream hook; prints the accumulated delta count + char count

using System.Runtime.CompilerServices;
using Vais.Agents;
using Vais.Agents.Core;

var provider = new ScriptedStreamingProvider();

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    StreamingFilters = [new TypingIndicatorFilter()],
});

// agent.StreamAsync(string) yields text deltas; streaming filters run transparently
await foreach (var delta in agent.StreamAsync("Tell me about agentic AI in a few sentences."))
{
    Console.Write(delta);
}
Console.WriteLine();

// ---- Filter ----
// OnStreamDeltaAsync fires on every filter BEFORE the delta is yielded to the outer consumer.
// InvokeAsync around-provider fires for the whole turn (before first delta → after last delta).
// OnStreamCompleteAsync fires after the accumulator drains, before output guardrails.
sealed class TypingIndicatorFilter : IStreamingAgentFilter
{
    private int _deltaCount;

    public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Console.WriteLine("[InvokeAsync] provider starting — around-provider hook");
        await foreach (var update in next(request, cancellationToken).ConfigureAwait(false))
            yield return update;
        Console.WriteLine("\n[InvokeAsync] provider stream drained");
    }

    public ValueTask<CompletionUpdate> OnStreamDeltaAsync(
        CompletionUpdate update, CancellationToken ct = default)
    {
        _deltaCount++;
        // fires BEFORE the delta reaches the outer consumer — mark it with '·'
        Console.Write('·');
        return ValueTask.FromResult(update);
    }

    public ValueTask OnStreamCompleteAsync(
        CompletionResponse final, CancellationToken ct = default)
    {
        Console.WriteLine();
        Console.WriteLine($"[OnStreamCompleteAsync] {_deltaCount} deltas, {final.Text.Length} chars accumulated");
        return ValueTask.CompletedTask;
    }
}

// ---- Scripted streaming provider (20 word-chunk deltas) ----
sealed class ScriptedStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
{
    private static readonly string[] Words =
        ("Agentic AI combines large language models with planning loops memory and tools " +
         "to accomplish complex multi-step tasks with minimal human intervention.").Split(' ');

    public string ProviderName => "scripted";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => throw new NotSupportedException("streaming only");

#pragma warning disable CS1998
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var word in Words)
        {
            ct.ThrowIfCancellationRequested();
            yield return new CompletionUpdate(word + " ");
        }
    }
#pragma warning restore CS1998
}
