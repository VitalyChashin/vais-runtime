// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class ToolCallEventEmissionTests
{
    [Fact]
    public async Task Dispatcher_Emits_Started_And_Completed_On_Success()
    {
        var bus = new InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var tool = new FakeTool("echo", _ => "ok");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool), toolGuardrails: null, eventBus: bus);
        var call = new ToolCallRequest("echo", JsonDocument.Parse("{}").RootElement, "c1");

        await dispatcher.DispatchAsync(call, AgentContext.Empty);

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<ToolCallStarted>()
            .Which.Should().Match<ToolCallStarted>(e => e.CallId == "c1" && e.ToolName == "echo");
        events[1].Should().BeOfType<ToolCallCompleted>()
            .Which.Should().Match<ToolCallCompleted>(e => e.CallId == "c1" && e.ToolName == "echo" && e.Succeeded && e.Error == null && e.Level == FailureLevel.Default);
    }

    [Fact]
    public async Task Dispatcher_Emits_Completed_With_Error_When_Tool_Throws()
    {
        var bus = new InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var tool = new FakeTool("boom", _ => throw new InvalidOperationException("broken"));
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool), toolGuardrails: null, eventBus: bus);
        var call = new ToolCallRequest("boom", JsonDocument.Parse("{}").RootElement, "c2");

        var outcome = await dispatcher.DispatchAsync(call, AgentContext.Empty);

        outcome.Error.Should().Be(nameof(InvalidOperationException));
        events.Should().HaveCount(2);
        events[1].Should().BeOfType<ToolCallCompleted>()
            .Which.Should().Match<ToolCallCompleted>(e =>
                e.Succeeded == false &&
                e.Error == nameof(InvalidOperationException) &&
                // A recovered tool failure (fed back to the model) is WARNING, not turn-fatal.
                e.Level == FailureLevel.Warning);
    }

    [Fact]
    public async Task Tool_Guardrail_Before_Deny_Emits_GuardrailTriggered_Not_ToolCallStarted()
    {
        var bus = new InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var tool = new FakeTool("t", _ => "never");
        var guardrail = new DenyingToolGuardrail(denyBefore: true, reason: "nope");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool), new[] { (IToolGuardrail)guardrail }, bus);
        var call = new ToolCallRequest("t", JsonDocument.Parse("{}").RootElement, "c3");

        Func<Task> act = async () => await dispatcher.DispatchAsync(call, AgentContext.Empty);
        await act.Should().ThrowAsync<AgentGuardrailDeniedException>();

        events.Should().ContainSingle()
            .Which.Should().BeOfType<GuardrailTriggered>()
            .Which.Should().Match<GuardrailTriggered>(e => e.Layer == GuardrailLayer.Tool && e.Reason == "nope");
    }

    [Fact]
    public async Task Tool_Guardrail_After_Deny_Emits_Paired_Events_Plus_GuardrailTriggered()
    {
        var bus = new InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var tool = new FakeTool("t", _ => "result");
        var guardrail = new DenyingToolGuardrail(denyAfter: true, reason: "post");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool), new[] { (IToolGuardrail)guardrail }, bus);
        var call = new ToolCallRequest("t", JsonDocument.Parse("{}").RootElement, "c4");

        Func<Task> act = async () => await dispatcher.DispatchAsync(call, AgentContext.Empty);
        await act.Should().ThrowAsync<AgentGuardrailDeniedException>();

        events.Should().HaveCount(3);
        events[0].Should().BeOfType<ToolCallStarted>();
        events[1].Should().BeOfType<ToolCallCompleted>()
            .Which.Succeeded.Should().BeTrue();  // tool ran successfully; the guardrail denied afterwards
        events[2].Should().BeOfType<GuardrailTriggered>()
            .Which.Layer.Should().Be(GuardrailLayer.Tool);
    }

    [Fact]
    public async Task Output_Guardrail_Deny_Emits_GuardrailTriggered_Via_StatefulAiAgent()
    {
        var bus = new InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var provider = new FakeCompletionProvider(_ => new CompletionResponse("bad"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            EventBus = bus,
            OutputGuardrails = new[] { new DenyingOutputGuardrail("forbidden") },
        });

        Func<Task> act = async () => await agent.AskAsync("hi");
        await act.Should().ThrowAsync<AgentGuardrailDeniedException>();

        var guardrailEvent = events.OfType<GuardrailTriggered>().Should().ContainSingle().Subject;
        guardrailEvent.Layer.Should().Be(GuardrailLayer.Output);
        guardrailEvent.Reason.Should().Be("forbidden");
    }

    // ---- helpers ----

    private sealed class InMemoryToolRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class FakeTool(string name, Func<JsonElement, string> invoke) : ITool
    {
        public string Name => name;
        public string Description => "fake";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(invoke(arguments));
    }

    private sealed class DenyingToolGuardrail(bool denyBefore = false, bool denyAfter = false, string? reason = null) : IToolGuardrail
    {
        public ValueTask<GuardrailOutcome> BeforeInvokeAsync(ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(denyBefore ? GuardrailOutcome.Deny(reason) : GuardrailOutcome.Pass);
        public ValueTask<GuardrailOutcome> AfterInvokeAsync(ITool tool, JsonElement arguments, string result, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(denyAfter ? GuardrailOutcome.Deny(reason) : GuardrailOutcome.Pass);
    }

    private sealed class DenyingOutputGuardrail(string reason) : IOutputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionResponse response, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Deny(reason));
    }
}
