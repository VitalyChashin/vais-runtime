// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// SequentialOrchestration — three participants piped; each receives the prior
// participant's output as its user message. Scripted providers so the behaviour
// is deterministic and the pipeline is observable.
// -----------------------------------------------------------------------------

var pipeline = new SequentialOrchestrator(new[]
{
    new AgentParticipant(
        Name: "researcher",
        Provider: new ScriptedProvider(reply: "Apollo 11 launched on 1969-07-16 from Kennedy Space Center; Armstrong and Aldrin landed on the Moon on 1969-07-20."),
        SystemPrompt: "Gather facts. Be thorough."),
    new AgentParticipant(
        Name: "writer",
        Provider: new ScriptedProvider(reply: "In July 1969, Apollo 11 carried Armstrong and Aldrin to the Moon, marking humanity's first crewed lunar landing."),
        SystemPrompt: "Turn facts into a one-paragraph summary."),
    new AgentParticipant(
        Name: "editor",
        Provider: new ScriptedProvider(reply: "Apollo 11 brought Armstrong and Aldrin to the Moon in July 1969 — humanity's first crewed lunar landing."),
        SystemPrompt: "Polish for tone and clarity."),
});

Console.WriteLine("pipeline: researcher → writer → editor");
Console.WriteLine();

await foreach (var step in pipeline.RunAsync("Summarise the Apollo programme in one paragraph."))
{
    Console.WriteLine($"[{step.AgentName}]");
    Console.WriteLine($"  {step.Text}");
    Console.WriteLine();
}

sealed class ScriptedProvider(string reply) : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(reply, ModelId: "fake-model"));
}
