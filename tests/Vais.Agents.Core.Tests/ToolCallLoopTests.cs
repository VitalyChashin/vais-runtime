// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class DefaultToolCallDispatcherTests
{
    [Fact]
    public async Task Unknown_Tool_Name_Returns_Outcome_With_Error()
    {
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry());
        var call = new ToolCallRequest("missing", JsonDocument.Parse("{}").RootElement, "c1");

        var outcome = await dispatcher.DispatchAsync(call, AgentContext.Empty);

        outcome.CallId.Should().Be("c1");
        outcome.Error.Should().Be(nameof(KeyNotFoundException));
        outcome.Result.Should().Contain("missing");
    }

    [Fact]
    public async Task Tool_Success_Returns_Outcome_With_Result_And_No_Error()
    {
        var tool = new FakeTool("echo", args => "tool-result");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool));
        var call = new ToolCallRequest("echo", JsonDocument.Parse("{}").RootElement, "c2");

        var outcome = await dispatcher.DispatchAsync(call, AgentContext.Empty);

        outcome.Result.Should().Be("tool-result");
        outcome.Error.Should().BeNull();
    }

    [Fact]
    public async Task Tool_Exception_Becomes_Outcome_Error()
    {
        var tool = new FakeTool("broken", _ => throw new InvalidOperationException("kaboom"));
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool));
        var call = new ToolCallRequest("broken", JsonDocument.Parse("{}").RootElement, "c3");

        var outcome = await dispatcher.DispatchAsync(call, AgentContext.Empty);

        outcome.Error.Should().Be(nameof(InvalidOperationException));
        outcome.Result.Should().Contain("kaboom");
    }

    [Fact]
    public async Task Tool_Guardrail_BeforeInvoke_Deny_Throws()
    {
        var invoked = false;
        var tool = new FakeTool("probe", _ => { invoked = true; return "done"; });
        var guardrail = new DenyingToolGuardrail(denyBefore: true, reason: "nope");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool), new[] { (IToolGuardrail)guardrail });

        var call = new ToolCallRequest("probe", JsonDocument.Parse("{}").RootElement, "c4");

        Func<Task> act = async () => await dispatcher.DispatchAsync(call, AgentContext.Empty);
        var thrown = await act.Should().ThrowAsync<AgentGuardrailDeniedException>();
        thrown.Which.Layer.Should().Be(GuardrailLayer.Tool);
        thrown.Which.Reason.Should().Be("nope");
        invoked.Should().BeFalse();
    }

    [Fact]
    public async Task Tool_Guardrail_AfterInvoke_Deny_Throws_After_Tool_Ran()
    {
        var invoked = false;
        var tool = new FakeTool("probe", _ => { invoked = true; return "done"; });
        var guardrail = new DenyingToolGuardrail(denyAfter: true, reason: "post-fact");
        var dispatcher = new DefaultToolCallDispatcher(new InMemoryToolRegistry(tool), new[] { (IToolGuardrail)guardrail });

        var call = new ToolCallRequest("probe", JsonDocument.Parse("{}").RootElement, "c5");

        Func<Task> act = async () => await dispatcher.DispatchAsync(call, AgentContext.Empty);
        await act.Should().ThrowAsync<AgentGuardrailDeniedException>();
        invoked.Should().BeTrue();
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
}

public sealed class StatefulAiAgentToolLoopTests
{
    [Fact]
    public async Task Single_Round_No_Tools_Behaves_Like_Before()
    {
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("final text"));
        var agent = new StatefulAiAgent(provider);

        var reply = await agent.AskAsync("hi");

        reply.Should().Be("final text");
        agent.Session.History.Should().HaveCount(2);
        agent.Session.History[0].Role.Should().Be(AgentChatRole.User);
        agent.Session.History[1].Role.Should().Be(AgentChatRole.Assistant);
    }

    [Fact]
    public async Task Tool_Call_Round_Loops_And_Returns_Final_Text()
    {
        // First provider call: returns a tool-call. Second call: returns final text.
        var tool = new RecordingTool("get_weather", args => "sunny");
        var provider = new SequencedProvider(
            new CompletionResponse("let me check", ToolCalls: new[]
            {
                new ToolCallRequest("get_weather", JsonDocument.Parse("{\"city\":\"Ankara\"}").RootElement, "c1"),
            }),
            new CompletionResponse("It is sunny in Ankara."));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(tool),
        });

        var reply = await agent.AskAsync("weather?");

        reply.Should().Be("It is sunny in Ankara.");
        tool.Invocations.Should().ContainSingle();
        // Session has only user + final assistant — tool + intermediate turns stay in working history.
        agent.Session.History.Should().HaveCount(2);
        agent.Session.History[0].Text.Should().Be("weather?");
        agent.Session.History[1].Text.Should().Be("It is sunny in Ankara.");

        // The second provider call must have seen the tool round trip in its history.
        provider.Received.Should().HaveCount(2);
        provider.Received[1].History.Should().HaveCount(3);
        provider.Received[1].History[1].Role.Should().Be(AgentChatRole.Assistant);
        provider.Received[1].History[1].ToolCalls.Should().NotBeNull();
        provider.Received[1].History[2].Role.Should().Be(AgentChatRole.Tool);
        provider.Received[1].History[2].ToolCallId.Should().Be("c1");
        provider.Received[1].History[2].Text.Should().Be("sunny");
    }

    [Fact]
    public async Task MaxTurns_Enforced_Throws_BudgetExceeded()
    {
        // Provider always returns a tool-call, never final text — loop would run forever.
        var tool = new RecordingTool("loop_tool", _ => "still here");
        var provider = new RepeatingProvider(() => new CompletionResponse("more?", ToolCalls: new[]
        {
            new ToolCallRequest("loop_tool", JsonDocument.Parse("{}").RootElement, Guid.NewGuid().ToString("N")),
        }));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(tool),
            Budget = new RunBudget(MaxTurns: 3),
        });

        Func<Task> act = async () => await agent.AskAsync("go");
        var thrown = await act.Should().ThrowAsync<AgentBudgetExceededException>();
        thrown.Which.BudgetField.Should().Be(nameof(RunBudget.MaxTurns));
        thrown.Which.Limit.Should().Be(3);
    }

    [Fact]
    public async Task MaxToolCalls_Enforced()
    {
        var tool = new RecordingTool("t", _ => "ok");
        var provider = new RepeatingProvider(() => new CompletionResponse("x", ToolCalls: new[]
        {
            new ToolCallRequest("t", JsonDocument.Parse("{}").RootElement, Guid.NewGuid().ToString("N")),
            new ToolCallRequest("t", JsonDocument.Parse("{}").RootElement, Guid.NewGuid().ToString("N")),
        }));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(tool),
            Budget = new RunBudget(MaxToolCalls: 3, MaxTurns: 10),
        });

        Func<Task> act = async () => await agent.AskAsync("go");
        var thrown = await act.Should().ThrowAsync<AgentBudgetExceededException>();
        thrown.Which.BudgetField.Should().Be(nameof(RunBudget.MaxToolCalls));
    }

    [Fact]
    public async Task MaxPromptTokens_Enforced_Summed_Across_Rounds()
    {
        var tool = new RecordingTool("t", _ => "ok");
        var calls = 0;
        var provider = new RepeatingProvider(() =>
        {
            calls++;
            return calls == 1
                ? new CompletionResponse("", PromptTokens: 60, ToolCalls: new[]
                    { new ToolCallRequest("t", JsonDocument.Parse("{}").RootElement, "c1") })
                : new CompletionResponse("final", PromptTokens: 60);
        });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(tool),
            Budget = new RunBudget(MaxPromptTokens: 100, MaxTurns: 10),
        });

        Func<Task> act = async () => await agent.AskAsync("go");
        var thrown = await act.Should().ThrowAsync<AgentBudgetExceededException>();
        thrown.Which.BudgetField.Should().Be(nameof(RunBudget.MaxPromptTokens));
        thrown.Which.Observed.Should().Be(120);
    }

    [Fact]
    public async Task Usage_Record_Reports_Aggregated_Tokens_Across_Rounds()
    {
        var recorded = new List<UsageRecord>();
        var sink = new RecordingUsageSink(recorded);

        var tool = new RecordingTool("t", _ => "r");
        var calls = 0;
        var provider = new RepeatingProvider(() =>
        {
            calls++;
            return calls == 1
                ? new CompletionResponse("", ModelId: "m", PromptTokens: 10, CompletionTokens: 5, ToolCalls: new[]
                    { new ToolCallRequest("t", JsonDocument.Parse("{}").RootElement, "c1") })
                : new CompletionResponse("final", ModelId: "m", PromptTokens: 15, CompletionTokens: 7);
        });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(tool),
            UsageSink = sink,
        });

        await agent.AskAsync("go");

        recorded.Should().ContainSingle();
        recorded[0].PromptTokens.Should().Be(25);
        recorded[0].CompletionTokens.Should().Be(12);
        recorded[0].ModelId.Should().Be("m");
        recorded[0].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Tool_Guardrail_Deny_Fails_The_Run_With_Typed_Exception()
    {
        var tool = new RecordingTool("t", _ => "r");
        var provider = new SequencedProvider(
            new CompletionResponse("", ToolCalls: new[]
                { new ToolCallRequest("t", JsonDocument.Parse("{}").RootElement, "c1") }),
            new CompletionResponse("never-reached"));
        var guardrail = new StaticToolGuardrail(denyBefore: true, reason: "policy");

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new InMemoryToolRegistry(tool),
            ToolGuardrails = new[] { (IToolGuardrail)guardrail },
        });

        Func<Task> act = async () => await agent.AskAsync("go");
        var thrown = await act.Should().ThrowAsync<AgentGuardrailDeniedException>();
        thrown.Which.Layer.Should().Be(GuardrailLayer.Tool);
        thrown.Which.Reason.Should().Be("policy");
        tool.Invocations.Should().BeEmpty();
    }

    // ---- helpers ----

    private sealed class InMemoryToolRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class RecordingTool(string name, Func<JsonElement, string> invoke) : ITool
    {
        public string Name => name;
        public string Description => "fake";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public List<JsonElement> Invocations { get; } = new();
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments);
            return Task.FromResult(invoke(arguments));
        }
    }

    private sealed class SequencedProvider(params CompletionResponse[] responses) : ICompletionProvider
    {
        private int _index;
        public string ProviderName => "sequenced";
        public List<CompletionRequest> Received { get; } = new();
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            Received.Add(request);
            if (_index >= responses.Length) throw new InvalidOperationException("SequencedProvider exhausted");
            return Task.FromResult(responses[_index++]);
        }
    }

    private sealed class RepeatingProvider(Func<CompletionResponse> factory) : ICompletionProvider
    {
        public string ProviderName => "repeating";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(factory());
    }

    private sealed class RecordingUsageSink(List<UsageRecord> store) : IUsageSink
    {
        public ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default)
        {
            store.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticToolGuardrail(bool denyBefore = false, bool denyAfter = false, string? reason = null) : IToolGuardrail
    {
        public ValueTask<GuardrailOutcome> BeforeInvokeAsync(ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(denyBefore ? GuardrailOutcome.Deny(reason) : GuardrailOutcome.Pass);
        public ValueTask<GuardrailOutcome> AfterInvokeAsync(ITool tool, JsonElement arguments, string result, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(denyAfter ? GuardrailOutcome.Deny(reason) : GuardrailOutcome.Pass);
    }
}
