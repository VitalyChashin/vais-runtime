// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

// -----------------------------------------------------------------------------
// HandoffBetweenAgents — consumer-driven handoff pattern. Triage agent detects
// "billing" keywords, publishes a HandoffRequested event on the shared bus,
// and a billing agent takes the continuation. Both agents share an event bus
// so the chain is visible.
// -----------------------------------------------------------------------------

var bus = new InMemoryAgentEventBus();
using var sub = bus.Subscribe((@event, ct) =>
{
    if (@event is HandoffRequested h)
        Console.WriteLine($"  [HANDOFF] {h.Handoff.FromAgent} → {h.Handoff.ToAgent}: {h.Handoff.Message}");
    return ValueTask.CompletedTask;
});

var triage = new StatefulAiAgent(
    new ScriptedProvider("This is a billing question — routing you to our billing team."),
    new StatefulAgentOptions
    {
        AgentName = "triage",
        EventBus = bus,
        SystemPrompt = "Classify the request; route billing issues to the billing team.",
    });

var billing = new StatefulAiAgent(
    new ScriptedProvider("Your invoice is attached — please check spam. Let me know if you need it re-sent."),
    new StatefulAgentOptions
    {
        AgentName = "billing",
        EventBus = bus,
        SystemPrompt = "You are a billing specialist. Address invoice questions directly.",
    });

var userMessage = "I can't find my invoice from last month.";
Console.WriteLine($"> {userMessage}");

// Step 1: triage replies + detects handoff.
var triageReply = await triage.AskAsync(userMessage);
Console.WriteLine($"  [triage] {triageReply}");

if (triageReply.Contains("billing", StringComparison.OrdinalIgnoreCase))
{
    var handoff = new Handoff(
        FromAgent: "triage",
        ToAgent: "billing",
        Message: userMessage,
        HistoryToCarry: null);   // exclude full history in this example
    await bus.PublishAsync(new HandoffRequested(DateTimeOffset.UtcNow,
        new AgentContext(), handoff));

    // Step 2: billing picks up the continuation with the user message.
    var billingReply = await billing.AskAsync(handoff.Message!);
    Console.WriteLine($"  [billing] {billingReply}");
}

sealed class ScriptedProvider(string reply) : ICompletionProvider
{
    public string ProviderName => "scripted";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => Task.FromResult(new CompletionResponse(reply, ModelId: "fake-model"));
}
