// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// ToolGuardrailsAndInterrupt — a tool guardrail that Interrupts when a
// destructive tool is requested. Shows the full HITL loop: run the agent,
// catch AgentInterruptedException, gather a synthetic human decision, resume.
// -----------------------------------------------------------------------------

var deleteTool = new DeleteFileTool();
var registry = new SingletonRegistry(deleteTool);

// The scripted provider emits a single tool-call "delete_file(name=report.pdf)"
// on turn 1. On resume, it emits the final answer on turn 2.
var provider = new ScriptedToolCallProvider();

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        ToolRegistry = registry,
        ToolGuardrails = new IToolGuardrail[] { new DestructiveToolApproval() },
    });

Console.WriteLine("> Delete the old quarterly report.");

try
{
    var reply = await agent.AskAsync("Delete the old quarterly report.");
    Console.WriteLine($"  reply: {reply}");
}
catch (AgentInterruptedException ex)
{
    Console.WriteLine($"  INTERRUPT {ex.Interrupt.InterruptId}");
    Console.WriteLine($"    reason: {ex.Interrupt.Reason}");
    Console.WriteLine($"    payload: {ex.Interrupt.Payload.GetRawText()}");

    // Simulate a human approval.
    Console.WriteLine("  (simulating human: approved)");
    var resume = new ResumeInput(ex.Interrupt.InterruptId,
        JsonSerializer.SerializeToElement(new { approved = true, by = "alice@example.com" }));

    // ResumeAsync (v0.4 shim) forwards the payload as the next user turn.
    provider.ArmFinalReply("Report deleted. Approval: alice@example.com.");
    var reply = await agent.ResumeAsync(resume);
    Console.WriteLine($"  reply: {reply}");
}

Console.WriteLine($"  tool invoked? {deleteTool.InvocationCount} time(s)");

// ---- Tool ----
sealed class DeleteFileTool : ITool
{
    public int InvocationCount { get; private set; }
    public string Name => "delete_file";
    public string Description => "Delete a file by name. Destructive.";
    public JsonElement ParametersSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""")
        .RootElement.Clone();
    public Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
    {
        InvocationCount++;
        return Task.FromResult("deleted.");
    }
}

// ---- Tool guardrail ----
sealed class DestructiveToolApproval : IToolGuardrail
{
    public ValueTask<GuardrailOutcome> BeforeInvokeAsync(
        ITool tool, JsonElement arguments, AgentContext ctx, CancellationToken ct = default)
    {
        if (!tool.Name.StartsWith("delete_", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(GuardrailOutcome.Pass);
        var interrupt = new AgentInterrupt(
            InterruptId: Guid.NewGuid().ToString("N"),
            Reason: $"approval required to run '{tool.Name}'",
            Payload: arguments);
        return ValueTask.FromResult(GuardrailOutcome.Interrupt(interrupt));
    }

    public ValueTask<GuardrailOutcome> AfterInvokeAsync(
        ITool tool, JsonElement arguments, string result, AgentContext ctx, CancellationToken ct = default)
        => ValueTask.FromResult(GuardrailOutcome.Pass);
}

// ---- Scripted provider ----
sealed class ScriptedToolCallProvider : ICompletionProvider
{
    private string? _finalReply;
    public string ProviderName => "scripted-tool-call";
    public void ArmFinalReply(string reply) => _finalReply = reply;

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        if (_finalReply is not null)
            return Task.FromResult(new CompletionResponse(_finalReply, ModelId: "fake-model"));

        // First call: request the tool.
        var call = new ToolCallRequest(
            ToolName: "delete_file",
            Arguments: JsonDocument.Parse("""{"name":"report.pdf"}""").RootElement.Clone(),
            CallId: "call-1");
        return Task.FromResult(new CompletionResponse(
            Text: "",
            ModelId: "fake-model",
            PromptTokens: null,
            CompletionTokens: null,
            ToolCalls: new[] { call }));
    }
}

sealed class SingletonRegistry(ITool tool) : IToolRegistry
{
    public IReadOnlyList<ITool> Tools { get; } = new[] { tool };
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}
