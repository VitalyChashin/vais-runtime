// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// PR 4 of the durable-execution pillar: <see cref="AgentInterrupt"/> and
/// <see cref="ResumeInput"/> gain <c>RunId</c>; <c>StatefulAiAgent.ResumeAsync</c>
/// threads the run id through so journaled tool outcomes cache-replay on
/// continuation; <see cref="ToolCallReplayed"/> event fires on cache hits.
/// </summary>
public sealed class DurableResumeTests
{
    [Fact]
    public void AgentInterrupt_RunId_Defaults_To_Null_On_Raw_Construction()
    {
        var interrupt = new AgentInterrupt("i1", "approve-me", EmptyArgs);
        interrupt.RunId.Should().BeNull();
    }

    [Fact]
    public void AgentInterrupt_RunId_Flows_Through_With_Expression()
    {
        var original = new AgentInterrupt("i1", "approve-me", EmptyArgs);
        var stamped = original with { RunId = "run-42" };

        stamped.InterruptId.Should().Be("i1");
        stamped.RunId.Should().Be("run-42");
        stamped.Should().NotBe(original, "RunId is part of record equality");
    }

    [Fact]
    public void ResumeInput_RunId_Defaults_To_Null()
    {
        var resume = new ResumeInput("i1", JsonDocument.Parse("\"yes\"").RootElement);
        resume.RunId.Should().BeNull();
    }

    [Fact]
    public async Task Tool_Guardrail_Interrupt_Stamps_RunId_Onto_The_Exception()
    {
        AgentInterrupt? raised = null;

        var interrupt = new AgentInterrupt("approve-send", "needs approval", EmptyArgs);
        var guardrail = new InterruptingToolGuardrail(interrupt);
        var tool = new FakeTool("send", _ => "sent");
        var dispatcher = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), new[] { (IToolGuardrail)guardrail });

        var ctx = AgentContext.Empty with { RunId = "run-from-agent" };
        var call = new ToolCallRequest("send", EmptyArgs, "c1");

        try
        {
            await dispatcher.DispatchAsync(call, ctx);
        }
        catch (AgentInterruptedException ex)
        {
            raised = ex.Interrupt;
        }

        raised.Should().NotBeNull();
        raised!.InterruptId.Should().Be("approve-send");
        raised.RunId.Should().Be("run-from-agent", "dispatcher must stamp RunId from context before throwing");
    }

    [Fact]
    public async Task Input_Guardrail_Interrupt_Stamps_RunId_Onto_The_Exception()
    {
        AgentInterrupt? raised = null;
        var interrupt = new AgentInterrupt("input-review", "review me", EmptyArgs);
        var guardrail = new InterruptingInputGuardrail(interrupt);
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("unused"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            InputGuardrails = new[] { (IInputGuardrail)guardrail },
            RunIdFactory = () => "run-from-factory",
        });

        try
        {
            await agent.AskAsync("hi");
        }
        catch (AgentInterruptedException ex)
        {
            raised = ex.Interrupt;
        }

        raised.Should().NotBeNull();
        raised!.RunId.Should().Be("run-from-factory");
    }

    [Fact]
    public async Task InterruptRaised_Event_Published_With_Stamped_RunId()
    {
        var bus = new RecordingEventBus();
        var interrupt = new AgentInterrupt("approve-send", "needs approval", EmptyArgs);
        var guardrail = new InterruptingToolGuardrail(interrupt);
        var tool = new FakeTool("send", _ => "sent");
        var dispatcher = new DefaultToolCallDispatcher(
            new FakeRegistry(tool), new[] { (IToolGuardrail)guardrail }, bus);

        var ctx = AgentContext.Empty with { RunId = "run-event" };

        try { await dispatcher.DispatchAsync(new ToolCallRequest("send", EmptyArgs, "c1"), ctx); }
        catch (AgentInterruptedException) { }

        var interruptEvent = bus.Events.OfType<InterruptRaised>().Should().ContainSingle().Subject;
        interruptEvent.Context.RunId.Should().Be("run-event",
            "context on the event carries the ambient RunId");
    }

    [Fact]
    public async Task ResumeAsync_With_RunId_Cache_Replays_Journaled_Tool_Call()
    {
        // Simulate a prior interrupted run that journaled one tool outcome,
        // then resume with the same RunId and observe that the cached outcome
        // is served without re-invoking the tool.
        var invocations = 0;
        var tool = new FakeTool("tool", _ => { invocations++; return $"result-{invocations}"; });

        var journal = new InMemoryAgentJournal();
        var runId = "paused-run-1";
        await journal.AppendAsync(new ToolCallRecorded(
            RunId: runId,
            CallId: "call-1",
            ToolName: "tool",
            Arguments: EmptyArgs,
            Outcome: new ToolCallOutcome("call-1", "cached-result"),
            At: DateTimeOffset.UtcNow));

        // Script: LLM asks for "tool" with CallId "call-1", then (after the cached
        // outcome comes back) produces a final answer.
        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("calling", ToolCalls: new[]
            {
                new ToolCallRequest("tool", EmptyArgs, "call-1"),
            }),
            new CompletionResponse("done"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new FakeRegistry(tool),
            Journal = journal,
        });

        var reply = await agent.ResumeAsync(new ResumeInput(
            InterruptId: "i1",
            Payload: JsonDocument.Parse("\"yes approved\"").RootElement)
        {
            RunId = runId,
        });

        reply.Should().Be("done");
        invocations.Should().Be(0, "journaled outcome must short-circuit the tool");
    }

    [Fact]
    public async Task ResumeAsync_Without_RunId_Skips_Cache_And_Reinvokes_Tool()
    {
        // With a null RunId on ResumeInput, the resume is shim-style: fresh run,
        // fresh RunId, the journaled outcome doesn't match and tool runs normally.
        var invocations = 0;
        var tool = new FakeTool("tool", _ => { invocations++; return "fresh-result"; });

        var journal = new InMemoryAgentJournal();
        await journal.AppendAsync(new ToolCallRecorded(
            RunId: "other-run",
            CallId: "call-1",
            ToolName: "tool",
            Arguments: EmptyArgs,
            Outcome: new ToolCallOutcome("call-1", "would-be-cached"),
            At: DateTimeOffset.UtcNow));

        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("calling", ToolCalls: new[]
            {
                new ToolCallRequest("tool", EmptyArgs, "call-1"),
            }),
            new CompletionResponse("done"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new FakeRegistry(tool),
            Journal = journal,
        });

        await agent.ResumeAsync(new ResumeInput(
            InterruptId: "i1",
            Payload: JsonDocument.Parse("\"approved\"").RootElement));

        invocations.Should().Be(1, "no RunId => no cache-replay => tool runs fresh");
    }

    [Fact]
    public async Task ResumeAsync_Rejects_Null_Or_Empty_Payload()
    {
        var agent = new StatefulAiAgent(
            new FakeCompletionProvider(_ => new CompletionResponse("x")),
            new StatefulAgentOptions());

        await FluentActions.Invoking(async () =>
                await agent.ResumeAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();

        await FluentActions.Invoking(async () =>
                await agent.ResumeAsync(new ResumeInput("i1", JsonDocument.Parse("\"\"").RootElement)))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task End_To_End_Interrupt_Then_Resume_Replays_Tool()
    {
        // Full round-trip: run once, tool-guardrail raises an interrupt, caller
        // catches AgentInterruptedException, pulls RunId from ex.Interrupt,
        // calls ResumeAsync with that RunId. The journal has no entries for
        // call-1 (the interrupt fired before the tool ran), so resume re-enters
        // the loop with the same RunId and the LLM can either skip the call or
        // ask for a fresh CallId; here we script it to ask for call-2 on resume.
        var invocations = new List<string>(); // Track which CallIds ran.
        var tool = new FakeTool("tool", _ => { invocations.Add("call"); return "ok"; });

        // Initial run: LLM asks for tool with call-1 → interrupt fires.
        // Resume: LLM sees the new user message, asks for tool with call-2 → runs.
        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("first", ToolCalls: new[]
            {
                new ToolCallRequest("tool", EmptyArgs, "call-1"),
            }),
            new CompletionResponse("after-resume", ToolCalls: new[]
            {
                new ToolCallRequest("tool", EmptyArgs, "call-2"),
            }),
            new CompletionResponse("final"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());

        var interruptPayload = new AgentInterrupt("needs-approval", "gated", EmptyArgs);
        var guardrail = new OneShotInterruptingToolGuardrail(interruptPayload);

        var journal = new InMemoryAgentJournal();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new FakeRegistry(tool),
            ToolGuardrails = new[] { (IToolGuardrail)guardrail },
            Journal = journal,
            RunIdFactory = () => "stable-run-id",
        });

        AgentInterruptedException? caught = null;
        try { await agent.AskAsync("please do it"); }
        catch (AgentInterruptedException ex) { caught = ex; }

        caught.Should().NotBeNull();
        caught!.Interrupt.RunId.Should().Be("stable-run-id");
        invocations.Should().BeEmpty("tool never ran -- guardrail interrupted before invocation");

        // Caller gathers input and resumes.
        var reply = await agent.ResumeAsync(new ResumeInput(
            InterruptId: caught.Interrupt.InterruptId,
            Payload: JsonDocument.Parse("\"approved, please proceed\"").RootElement)
        {
            RunId = caught.Interrupt.RunId,
        });

        reply.Should().Be("final");
        invocations.Should().HaveCount(1, "call-2 runs fresh on resume");
    }

    [Fact]
    public async Task ResumeAsync_Emits_ToolCallReplayed_Event_On_Cache_Hit()
    {
        var bus = new RecordingEventBus();
        var tool = new FakeTool("tool", _ => "live");
        var journal = new InMemoryAgentJournal();
        await journal.AppendAsync(new ToolCallRecorded(
            RunId: "run-1",
            CallId: "c1",
            ToolName: "tool",
            Arguments: EmptyArgs,
            Outcome: new ToolCallOutcome("c1", "cached"),
            At: DateTimeOffset.UtcNow));

        var scripted = new Queue<CompletionResponse>(new[]
        {
            new CompletionResponse("calling", ToolCalls: new[]
            {
                new ToolCallRequest("tool", EmptyArgs, "c1"),
            }),
            new CompletionResponse("done"),
        });
        var provider = new FakeCompletionProvider(_ => scripted.Dequeue());

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolRegistry = new FakeRegistry(tool),
            Journal = journal,
            EventBus = bus,
        });

        await agent.ResumeAsync(new ResumeInput("i1", JsonDocument.Parse("\"ok\"").RootElement)
        {
            RunId = "run-1",
        });

        var replay = bus.Events.OfType<ToolCallReplayed>().Should().ContainSingle().Subject;
        replay.CallId.Should().Be("c1");
        replay.ToolName.Should().Be("tool");
        // No paired ToolCallStarted/ToolCallCompleted for the cached call.
        bus.Events.OfType<ToolCallStarted>().Where(e => e.CallId == "c1").Should().BeEmpty();
        bus.Events.OfType<ToolCallCompleted>().Where(e => e.CallId == "c1").Should().BeEmpty();
    }

    [Fact]
    public void ToolCallReplayed_Is_A_Record_With_Value_Equality()
    {
        var ts = DateTimeOffset.UtcNow;
        var ctx = AgentContext.Empty with { RunId = "r1" };
        var a = new ToolCallReplayed(ts, ctx, "c1", "tool");
        var b = new ToolCallReplayed(ts, ctx, "c1", "tool");
        var c = new ToolCallReplayed(ts, ctx, "c2", "tool");

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    // ---- helpers ----

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private sealed class FakeRegistry(params ITool[] tools) : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools { get; } = tools;
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class FakeTool(string name, Func<JsonElement, string> invoke) : ITool
    {
        public string Name => name;
        public string Description => "fake";
        public JsonElement ParametersSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(invoke(arguments));
    }

    private sealed class InterruptingToolGuardrail(AgentInterrupt interrupt) : IToolGuardrail
    {
        public ValueTask<GuardrailOutcome> BeforeInvokeAsync(ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Interrupt(interrupt));

        public ValueTask<GuardrailOutcome> AfterInvokeAsync(ITool tool, JsonElement arguments, string result, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Pass);
    }

    /// <summary>Interrupts on the first BeforeInvoke call, then passes.</summary>
    private sealed class OneShotInterruptingToolGuardrail(AgentInterrupt interrupt) : IToolGuardrail
    {
        private int _fires;

        public ValueTask<GuardrailOutcome> BeforeInvokeAsync(ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default)
        {
            _fires++;
            return ValueTask.FromResult(_fires == 1
                ? GuardrailOutcome.Interrupt(interrupt)
                : GuardrailOutcome.Pass);
        }

        public ValueTask<GuardrailOutcome> AfterInvokeAsync(ITool tool, JsonElement arguments, string result, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Pass);
    }

    private sealed class InterruptingInputGuardrail(AgentInterrupt interrupt) : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Interrupt(interrupt));
    }

    private sealed class RecordingEventBus : IAgentEventBus
    {
        public List<AgentEvent> Events { get; } = new();
        public ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return ValueTask.CompletedTask;
        }
        public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
            => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
