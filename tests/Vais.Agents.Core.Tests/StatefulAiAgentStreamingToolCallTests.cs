// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Coverage for tool-using streaming — the v0.4.1 follow-up that gave
/// <see cref="StatefulAiAgent.StreamAsync(string, CancellationToken)"/> an outer tool-call loop parallel
/// to <see cref="StatefulAiAgent.AskAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses a scripted streaming provider that cycles through a per-call script
/// queue, so a single test can drive multiple streamed turns — one per
/// tool-call round plus the final answer.
/// </para>
/// </remarks>
public sealed class StatefulAiAgentStreamingToolCallTests
{
    [Fact]
    public async Task StreamAsync_Dispatches_Tool_Call_And_Continues_Streaming_Next_Turn()
    {
        var tool = new EchoTool();
        // First streamed turn: model emits preamble text AND ends with a terminal
        // tool-call update (realistic shape — "Let me look that up... [tool]").
        // Second streamed turn: final answer, no tool calls.
        var provider = new ScriptedMultiTurnStreamingProvider(
            new[]
            {
                new CompletionUpdate("Looking up... "),
                new CompletionUpdate(string.Empty, ToolCalls: new[]
                {
                    new ToolCallRequest("echo", JsonDocument.Parse("""{"text":"hi"}""").RootElement, "call-1"),
                }),
            },
            new[] { new CompletionUpdate("Got: "), new CompletionUpdate("hi.", ModelId: "m1", PromptTokens: 9, CompletionTokens: 4) });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new SingleToolRegistry(tool),
        });

        var collected = new List<string>();
        await foreach (var delta in agent.StreamAsync("say hi via tool"))
        {
            collected.Add(delta);
        }

        // Consumer sees text deltas across both streamed turns; the terminal
        // tool-call-only update contributes no text.
        string.Concat(collected).Should().Be("Looking up... Got: hi.");

        // Tool was dispatched with the model-requested args.
        tool.Invocations.Should().ContainSingle();
        tool.Invocations[0].GetProperty("text").GetString().Should().Be("hi");

        // Session stays clean: only the user turn + final assistant text from
        // the last streamed turn. Intermediate assistant-with-tool-calls and
        // tool-role turns lived in the working history for the run only.
        agent.History.Should().HaveCount(2);
        agent.History[0].Should().Be(new ChatTurn(AgentChatRole.User, "say hi via tool"));
        agent.History[1].Should().Be(new ChatTurn(AgentChatRole.Assistant, "Got: hi."));

        // Provider was called twice — once per streamed turn.
        provider.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task StreamAsync_Working_History_Carries_Tool_Results_Into_Next_Turn()
    {
        var tool = new EchoTool();
        var provider = new ScriptedMultiTurnStreamingProvider(
            // First streamed turn: model requests a tool.
            new[] { new CompletionUpdate("...", ToolCalls: new[]
                {
                    new ToolCallRequest("echo", JsonDocument.Parse("""{"text":"hello"}""").RootElement, "call-a"),
                }) },
            // Second streamed turn: final answer.
            new[] { new CompletionUpdate("ok") });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new SingleToolRegistry(tool),
        });

        await foreach (var _ in agent.StreamAsync("run")) { }

        // The second streamed turn's request must include: (user, assistant-with-tool-calls, tool-result).
        provider.RequestsSeen.Should().HaveCount(2);
        var secondTurnHistory = provider.RequestsSeen[1].History;
        secondTurnHistory.Should().HaveCount(3);
        secondTurnHistory[0].Role.Should().Be(AgentChatRole.User);
        secondTurnHistory[1].Role.Should().Be(AgentChatRole.Assistant);
        secondTurnHistory[1].ToolCalls.Should().NotBeNull();
        secondTurnHistory[1].ToolCalls![0].CallId.Should().Be("call-a");
        secondTurnHistory[2].Role.Should().Be(AgentChatRole.Tool);
        secondTurnHistory[2].ToolCallId.Should().Be("call-a");
    }

    [Fact]
    public async Task StreamAsync_MaxToolCalls_Budget_Aborts_The_Run()
    {
        var tool = new EchoTool();
        // Each streamed turn emits one tool call; cap at 1 tool call total so the
        // second tool request (second streamed turn) breaches the budget.
        var provider = new ScriptedMultiTurnStreamingProvider(
            new[] { new CompletionUpdate(string.Empty, ToolCalls: new[]
                {
                    new ToolCallRequest("echo", JsonDocument.Parse("""{"text":"a"}""").RootElement, "call-1"),
                }) },
            new[] { new CompletionUpdate(string.Empty, ToolCalls: new[]
                {
                    new ToolCallRequest("echo", JsonDocument.Parse("""{"text":"b"}""").RootElement, "call-2"),
                }) });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new SingleToolRegistry(tool),
            Budget = new RunBudget(MaxToolCalls: 1),
        });

        Func<Task> act = async () =>
        {
            await foreach (var _ in agent.StreamAsync("go")) { }
        };

        (await act.Should().ThrowAsync<AgentBudgetExceededException>())
            .Which.BudgetField.Should().Be(nameof(RunBudget.MaxToolCalls));

        // First tool dispatched before the budget tripped; second was rejected.
        tool.Invocations.Should().HaveCount(1);
    }

    [Fact]
    public async Task StreamAsync_MaxTurns_Budget_Aborts_Before_Second_Stream_Starts()
    {
        var tool = new EchoTool();
        var provider = new ScriptedMultiTurnStreamingProvider(
            new[] { new CompletionUpdate(string.Empty, ToolCalls: new[]
                {
                    new ToolCallRequest("echo", JsonDocument.Parse("""{"text":"x"}""").RootElement, "call-1"),
                }) },
            new[] { new CompletionUpdate("never reached") });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new SingleToolRegistry(tool),
            Budget = new RunBudget(MaxTurns: 1),
        });

        Func<Task> act = async () =>
        {
            await foreach (var _ in agent.StreamAsync("go")) { }
        };

        (await act.Should().ThrowAsync<AgentBudgetExceededException>())
            .Which.BudgetField.Should().Be(nameof(RunBudget.MaxTurns));

        // Tool-call round 1 ran; its dispatch drove workingHistory forward but the
        // budget check at the top of iteration 2 tripped before the provider was
        // called a second time.
        provider.CallCount.Should().Be(1);
        tool.Invocations.Should().HaveCount(1);
    }

    [Fact]
    public async Task StreamAsync_Publishes_Tool_Call_Events_Via_Dispatcher()
    {
        var tool = new EchoTool();
        var eventBus = new InProcAgentEventBus();
        var provider = new ScriptedMultiTurnStreamingProvider(
            new[] { new CompletionUpdate(string.Empty, ToolCalls: new[]
                {
                    new ToolCallRequest("echo", JsonDocument.Parse("""{"text":"q"}""").RootElement, "call-x"),
                }) },
            new[] { new CompletionUpdate("done") });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new SingleToolRegistry(tool),
            EventBus = eventBus,
        });

        await foreach (var _ in agent.StreamAsync("use tool")) { }

        // Full event envelope for the run: TurnStarted + ToolCallStarted + ToolCallCompleted + TurnCompleted.
        eventBus.Events.Select(e => e.GetType().Name).Should().Equal(
            nameof(TurnStarted),
            nameof(ToolCallStarted),
            nameof(ToolCallCompleted),
            nameof(TurnCompleted));
    }

    private sealed class EchoTool : ITool
    {
        private static readonly JsonElement s_schema = JsonDocument.Parse(
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""")
            .RootElement.Clone();

        public List<JsonElement> Invocations { get; } = new();

        public string Name => "echo";
        public string Description => "Echo the supplied text.";
        public JsonElement ParametersSchema => s_schema;

        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(arguments.Clone());
            return Task.FromResult(arguments.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty);
        }
    }

    private sealed class SingleToolRegistry : IToolRegistry
    {
        public SingleToolRegistry(ITool tool) { Tools = new[] { tool }; }
        public IReadOnlyList<ITool> Tools { get; }
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class InProcAgentEventBus : IAgentEventBus
    {
        public List<AgentEvent> Events { get; } = new();

        public ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return ValueTask.CompletedTask;
        }

        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
            => new NoopSub();

        private sealed class NoopSub : IDisposable { public void Dispose() { } }
    }
}
