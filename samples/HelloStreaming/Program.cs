// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// HelloStreaming — basic StatefulAiAgent.StreamAsync with a scripted provider.
// Deltas print character-by-character to show the streamed consumer surface.
// No tools on this turn — see HelloStreamingTools for the tool-using variant.
// -----------------------------------------------------------------------------

var provider = new ScriptedStreamingProvider(new[]
{
    new CompletionUpdate("Once "),
    new CompletionUpdate("upon "),
    new CompletionUpdate("a "),
    new CompletionUpdate("time, "),
    new CompletionUpdate("a .NET developer "),
    new CompletionUpdate("wrote a sample.",
        ModelId: "fake-streaming", PromptTokens: 6, CompletionTokens: 12),
});

var agent = new StatefulAiAgent(provider);

Console.Write("reply: ");
await foreach (var delta in agent.StreamAsync("tell me a one-sentence story"))
{
    Console.Write(delta);
    await Task.Delay(50);   // visible pacing so the stream feel is obvious
}
Console.WriteLine();
Console.WriteLine();
Console.WriteLine("session history:");
foreach (var turn in agent.History)
    Console.WriteLine($"  [{turn.Role}] {turn.Text}");

// ---- Scripted streaming provider ----
sealed class ScriptedStreamingProvider(IEnumerable<CompletionUpdate> updates)
    : ICompletionProvider, IStreamingCompletionProvider
{
    public string ProviderName => "scripted-streaming";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(string.Concat(updates.Select(u => u.TextDelta)), ModelId: "fake-streaming"));

#pragma warning disable CS1998
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var u in updates)
        {
            ct.ThrowIfCancellationRequested();
            yield return u;
        }
    }
#pragma warning restore CS1998
}
