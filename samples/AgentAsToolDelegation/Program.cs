// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;

// -----------------------------------------------------------------------------
// AgentAsToolDelegation — wires LocalAgentTool so a coordinator agent can
// delegate to a math-specialist agent running in the same in-process runtime.
//
// Flow:
//   1. Coordinator receives the user question.
//   2. The coordinator's scripted provider emits a "call_math_specialist" tool call.
//   3. LocalAgentTool creates a fresh deterministic session in InMemoryAgentRuntime,
//      invokes the math-specialist, and returns its answer to the coordinator.
//   4. The coordinator's provider sees the tool result and produces the final reply.
//   5. The specialist's session is removed (no accumulated state leaks).
// -----------------------------------------------------------------------------

// ── Specialist agent provider ────────────────────────────────────────────────

var specialistProvider = new SequencedProvider(
    new CompletionResponse("42 × 7 = 294"));

// ── Shared runtime ───────────────────────────────────────────────────────────

// InMemoryAgentRuntime is dev/test-only; the same IAgentRuntime contract is
// fulfilled by the Orleans grain runtime in production.
var runtime = new InMemoryAgentRuntime(
    specialistProvider,
    id => new StatefulAgentOptions
    {
        AgentName = id,
        SystemPrompt = "You are a math specialist. Answer arithmetic questions directly.",
    });

// ── LocalAgentTool ───────────────────────────────────────────────────────────

// Wraps the "math-specialist" agent as an ITool the coordinator can call.
// runtimeFactory is a lambda to avoid a DI cycle: the factory is captured at
// tool-creation time and resolved lazily at each invocation.
var mathTool = new LocalAgentTool(
    runtimeFactory: () => runtime,
    effectiveAgentId: "math-specialist",
    name: "call_math_specialist",
    description: "Delegate an arithmetic question to the math-specialist sub-agent.",
    allowCallerSuppliedSession: false,
    propagateAllowedTools: true);

Console.WriteLine("Schema the coordinator model sees for the math tool:");
Console.WriteLine($"  {mathTool.ParametersSchema.GetRawText()}");
Console.WriteLine();

// ── Coordinator agent ────────────────────────────────────────────────────────

// Scripted provider simulates a two-turn model run:
//   Turn 1: model asks for a tool call.
//   Turn 2: model produces the final answer after receiving the tool result.
var toolCallArgs = JsonDocument.Parse("""{"message":"What is 42 × 7?"}""").RootElement;
var coordinatorProvider = new SequencedProvider(
    new CompletionResponse("", ToolCalls: new[]
    {
        new ToolCallRequest("call_math_specialist", toolCallArgs, CallId: "tc-1"),
    }),
    new CompletionResponse("The math specialist confirmed: 42 × 7 = 294."));

var coordinator = new StatefulAiAgent(
    coordinatorProvider,
    new StatefulAgentOptions
    {
        AgentName = "coordinator",
        SystemPrompt = "You coordinate tasks. Delegate arithmetic to call_math_specialist.",
        ToolRegistry = new SimpleRegistry(mathTool),
    });

// ── Run ──────────────────────────────────────────────────────────────────────

Console.WriteLine("User → coordinator: \"What is 42 × 7?\"");
var reply = await coordinator.AskAsync("What is 42 × 7?");
Console.WriteLine($"Coordinator → user: \"{reply}\"");
Console.WriteLine();
Console.WriteLine("Round-trip complete — math-specialist was invoked in-process via LocalAgentTool.");

// ── Helpers ──────────────────────────────────────────────────────────────────

sealed class SequencedProvider(params CompletionResponse[] responses) : ICompletionProvider
{
    private int _index;
    public string ProviderName => "sequenced";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        var idx = Interlocked.Increment(ref _index) - 1;
        return Task.FromResult(idx < responses.Length ? responses[idx] : responses[^1]);
    }
}

sealed class SimpleRegistry(params ITool[] tools) : IToolRegistry
{
    public IReadOnlyList<ITool> Tools { get; } = tools;
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}
