// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Polly;
using Polly.Retry;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.12 PR 2: StatefulAiAgent implements IStreamingAiAgent; exposes the full
/// AgentEvent taxonomy via StreamAsync(userMessage, context, ct). Covers event
/// ordering, tool-call interleaving, guardrail denial, interrupt, cancellation,
/// context stamping, source-compat with the string-returning overload, and retry
/// deduplication.
/// </summary>
public sealed class StatefulAiAgentStreamingEventsTests
{
    [Fact]
    public async Task Simple_Text_Turn_Yields_TurnStarted_Deltas_TurnCompleted()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("Hello, "),
            new CompletionUpdate("world!", ModelId: "fake-model", PromptTokens: 10, CompletionTokens: 3),
        });
        var agent = new StatefulAiAgent(provider);
        var events = await DrainAsync(agent, "hi");

        events[0].Should().BeOfType<TurnStarted>().Which.UserMessage.Should().Be("hi");
        var deltas = events.OfType<CompletionDelta>().ToList();
        deltas.Should().HaveCount(2);
        deltas[0].TextDelta.Should().Be("Hello, ");
        deltas[1].TextDelta.Should().Be("world!");
        deltas[1].ModelId.Should().Be("fake-model");

        events[^1].Should().BeOfType<TurnCompleted>().Which.AssistantText.Should().Be("Hello, world!");
        var completed = (TurnCompleted)events[^1];
        completed.PromptTokens.Should().Be(10);
        completed.CompletionTokens.Should().Be(3);
    }

    [Fact]
    public async Task Tool_Call_Turn_Interleaves_Tool_Events_With_Deltas()
    {
        var callId = "call-1";
        var argsJson = JsonDocument.Parse("""{"x":1}""").RootElement;
        var scripted = new ScriptedMultiTurnStreamingProvider(
            new[]
            {
                new CompletionUpdate(string.Empty, ToolCalls: new[]
                {
                    new ToolCallRequest("echo", argsJson, callId),
                }),
            },
            new[]
            {
                new CompletionUpdate("ok"),
            });

        var tool = Tool.FromFunc<string, string>("echo", "echo", (input, _) => Task.FromResult("echo-result"));
        var registry = new InMemoryToolRegistry(new[] { tool });
        var agent = new StatefulAiAgent(scripted, new StatefulAgentOptions
        {
            ToolRegistry = registry,
        });

        var events = await DrainAsync(agent, "use echo");
        var kinds = events.Select(e => e.GetType().Name).ToList();

        // TurnStarted → CompletionDelta (terminal with ToolCalls) → ToolCallStarted → ToolCallCompleted → CompletionDelta(ok) → TurnCompleted
        kinds.Should().ContainInOrder(new[]
        {
            nameof(TurnStarted),
            nameof(ToolCallStarted),
            nameof(ToolCallCompleted),
            nameof(TurnCompleted),
        });
        var started = events.OfType<ToolCallStarted>().Single();
        var completed = events.OfType<ToolCallCompleted>().Single();
        started.CallId.Should().Be(callId);
        started.ToolName.Should().Be("echo");
        completed.CallId.Should().Be(callId);
        completed.ToolName.Should().Be("echo");
    }

    [Fact]
    public async Task Guardrail_Denied_Yields_GuardrailTriggered_Then_TurnFailed()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("x") });
        var denying = new DenyingInputGuardrail();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            InputGuardrails = new IInputGuardrail[] { denying },
        });

        Exception? thrown = null;
        var collected = new List<AgentEvent>();
        try
        {
            await foreach (var e in ((IStreamingAiAgent)agent).StreamAsync("hi", new AgentContext(), default))
            {
                collected.Add(e);
            }
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.Should().BeOfType<AgentGuardrailDeniedException>();
        collected[0].Should().BeOfType<TurnStarted>();
        collected.OfType<CompletionDelta>().Should().BeEmpty();
        collected.OfType<GuardrailTriggered>().Should().ContainSingle()
            .Which.Layer.Should().Be(GuardrailLayer.Input);
        collected[^1].Should().BeOfType<TurnFailed>();
    }

    [Fact]
    public async Task Interrupt_Yields_InterruptRaised_Then_TurnFailed()
    {
        var callId = "call-int";
        var argsJson = JsonDocument.Parse("""{}""").RootElement;
        var scripted = new ScriptedMultiTurnStreamingProvider(
            new[]
            {
                new CompletionUpdate(string.Empty, ToolCalls: new[]
                {
                    new ToolCallRequest("interrupting", argsJson, callId),
                }),
            });

        var interruptingGuardrail = new InterruptingToolGuardrail();
        var tool = Tool.FromFunc<string, string>("interrupting", "never-runs", (_, _) => Task.FromResult("unreached"));
        var registry = new InMemoryToolRegistry(new[] { tool });
        var agent = new StatefulAiAgent(scripted, new StatefulAgentOptions
        {
            ToolRegistry = registry,
            ToolGuardrails = new IToolGuardrail[] { interruptingGuardrail },
        });

        Exception? thrown = null;
        var collected = new List<AgentEvent>();
        try
        {
            await foreach (var e in ((IStreamingAiAgent)agent).StreamAsync("please", new AgentContext(), default))
            {
                collected.Add(e);
            }
        }
        catch (Exception ex) { thrown = ex; }

        thrown.Should().BeOfType<AgentInterruptedException>();
        collected.OfType<InterruptRaised>().Should().ContainSingle()
            .Which.Reason.Should().Be("human-in-the-loop");
        collected[^1].Should().BeOfType<TurnFailed>();
    }

    [Fact]
    public async Task Cancelled_Mid_Delta_Ends_Without_TurnFailed()
    {
        using var cts = new CancellationTokenSource();
        // Provider yields forever; agent relies on the CT passing through its
        // provider call (ThrowIfCancellationRequested fires inside the fake's
        // per-yield check, so OCE propagates up when the test cancels mid-stream).
        var provider = new FakeStreamingCompletionProvider(_ => InfiniteYield());
        var agent = new StatefulAiAgent(provider);

        Exception? thrown = null;
        var collected = new List<AgentEvent>();
        try
        {
            await foreach (var e in ((IStreamingAiAgent)agent).StreamAsync("hi", new AgentContext(), cts.Token))
            {
                collected.Add(e);
                if (e is CompletionDelta)
                {
                    cts.Cancel();
                }
            }
        }
        catch (OperationCanceledException ex) { thrown = ex; }

        thrown.Should().BeOfType<OperationCanceledException>();
        collected.OfType<TurnFailed>().Should().BeEmpty("cancellation is not a turn failure");

        static IEnumerable<CompletionUpdate> InfiniteYield()
        {
            while (true)
            {
                yield return new CompletionUpdate("x ");
            }
        }
    }

    [Fact]
    public async Task CompletionDelta_Context_Carries_RunId_From_TurnStarted()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("ok") });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            RunIdFactory = () => "fixed-run",
        });

        var events = await DrainAsync(agent, "hi");

        var turnStarted = events.OfType<TurnStarted>().Single();
        var delta = events.OfType<CompletionDelta>().First();
        var turnCompleted = events.OfType<TurnCompleted>().Single();

        turnStarted.Context.RunId.Should().Be("fixed-run");
        delta.Context.RunId.Should().Be("fixed-run");
        turnCompleted.Context.RunId.Should().Be("fixed-run");
    }

    [Fact]
    public async Task StreamAsync_String_Overload_Source_Compat_Projects_To_Text()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("alpha "),
            new CompletionUpdate("beta ", ModelId: "m"),
            new CompletionUpdate(string.Empty), // metadata-only chunk — filtered out by string overload
            new CompletionUpdate("gamma"),
        });
        var agent = new StatefulAiAgent(provider);

        var strings = new List<string>();
        await foreach (var s in agent.StreamAsync("hi"))
        {
            strings.Add(s);
        }

        strings.Should().Equal("alpha ", "beta ", "gamma");
    }

    [Fact]
    public async Task Retry_On_Pre_First_Delta_Failure_Emits_Single_TurnStarted()
    {
        var provider = new FlakyProvider(
            attempts: new Func<IAsyncEnumerable<CompletionUpdate>>[]
            {
                () => throw new HttpRequestException("transient"),
                () => YieldAsync("retry-succeeded"),
            });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingResiliencePipeline = ZeroDelayPipeline(),
        });

        var events = await DrainAsync(agent, "hi");
        events.OfType<TurnStarted>().Should().ContainSingle();
        events.OfType<TurnCompleted>().Should().ContainSingle()
            .Which.AssistantText.Should().Be("retry-succeeded");
        provider.Attempts.Should().Be(2);

#pragma warning disable CS1998
        static async IAsyncEnumerable<CompletionUpdate> YieldAsync(string text)
        {
            yield return new CompletionUpdate(text);
        }
#pragma warning restore CS1998
    }

    // ---- helpers ----

    private static async Task<List<AgentEvent>> DrainAsync(StatefulAiAgent agent, string userMessage)
    {
        var events = new List<AgentEvent>();
        await foreach (var e in ((IStreamingAiAgent)agent).StreamAsync(userMessage, new AgentContext(), default))
        {
            events.Add(e);
        }
        return events;
    }

    private static Polly.ResiliencePipeline ZeroDelayPipeline() =>
        new Polly.ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = Polly.DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                ShouldHandle = new Polly.PredicateBuilder().Handle<Exception>(),
            })
            .Build();

    private sealed class InMemoryToolRegistry : IToolRegistry
    {
        public InMemoryToolRegistry(IReadOnlyList<ITool> tools) { Tools = tools; }
        public IReadOnlyList<ITool> Tools { get; }
        public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
    }

    private sealed class DenyingInputGuardrail : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(GuardrailOutcome.Deny("policy denies"));
    }

    private sealed class InterruptingToolGuardrail : IToolGuardrail
    {
        public ValueTask<GuardrailOutcome> BeforeInvokeAsync(ITool tool, JsonElement arguments, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Interrupt(
                new AgentInterrupt("int-1", "human-in-the-loop", JsonDocument.Parse("{}").RootElement),
                reason: "human-in-the-loop"));

        public ValueTask<GuardrailOutcome> AfterInvokeAsync(ITool tool, JsonElement arguments, string result, AgentContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(GuardrailOutcome.Pass);
    }

    private sealed class FlakyProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        private readonly Func<IAsyncEnumerable<CompletionUpdate>>[] _attempts;
        public int Attempts { get; private set; }

        public FlakyProvider(Func<IAsyncEnumerable<CompletionUpdate>>[] attempts) { _attempts = attempts; }

        public string ProviderName => "flaky";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            var idx = Attempts;
            Attempts++;
            return _attempts[idx]();
        }
    }
}
