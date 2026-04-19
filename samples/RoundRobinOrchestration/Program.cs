// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// RoundRobinOrchestration — two participants debate for up to 3 rounds. A
// TerminationPredicate stops early if either side says "agreed". Each
// participant sees the shared conversation (prior steps encoded as assistant
// turns) before emitting its next step.
// -----------------------------------------------------------------------------

var optimist = new CycledProvider(new[]
{
    "I think we should ship on Friday — the features are stable.",
    "Point taken, but the bug list is manageable; we can hotfix on Monday.",
    "Ok — if we agreed on a Friday ship with a hotfix plan, I'm in.",
});
var pessimist = new CycledProvider(new[]
{
    "I disagree — we still have two open P1 bugs and on-call is light.",
    "A hotfix on Monday is risky when customers might hit the bugs over the weekend.",
    "If we're serious about the hotfix plan and we boost on-call, then agreed.",
});

var debate = new RoundRobinOrchestrator(
    participants: new[]
    {
        new AgentParticipant("optimist",  optimist),
        new AgentParticipant("pessimist", pessimist),
    },
    maxRounds: 3,
    terminate: steps => steps.Any(s =>
        s.Text.Contains("agreed", StringComparison.OrdinalIgnoreCase)));

await foreach (var step in debate.RunAsync("Should we ship on Friday?"))
{
    Console.WriteLine($"[{step.AgentName}] {step.Text}");
    Console.WriteLine();
}

// Each call rotates through pre-scripted replies — so the debate converges
// deterministically.
sealed class CycledProvider(IReadOnlyList<string> replies) : ICompletionProvider
{
    private int _turn;
    public string ProviderName => "cycled";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var reply = replies[Math.Min(_turn, replies.Count - 1)];
        _turn++;
        return Task.FromResult(new CompletionResponse(reply, ModelId: "fake-model"));
    }
}
