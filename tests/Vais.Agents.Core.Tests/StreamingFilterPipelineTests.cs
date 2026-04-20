// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Polly;
using Polly.Retry;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Covers the v0.10 streaming-filter around-provider pipeline + streaming-side retry
/// boundary. Sister suite to <see cref="StreamingFilterTests"/> (per-delta hook) and
/// <see cref="StatefulAiAgentStreamingTests"/> (baseline streaming).
/// </summary>
public sealed class StreamingFilterPipelineTests
{
    // ---- 1: short-circuit via cache ----

    [Fact]
    public async Task Around_Provider_Filter_Short_Circuits_Yields_Cached_Chunks_Without_Invoking_Provider()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("SHOULD NOT APPEAR") });
        var cacheFilter = new CachedStreamingFilter(new[] { "c1", "c2", "c3" });

        // Per-delta hook on same filter — asserts agent fires it on the filter's own yielded chunks.
        var deltaObserved = new List<string>();
        cacheFilter.OnDelta = update => deltaObserved.Add(update.TextDelta);

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new IStreamingAgentFilter[] { cacheFilter },
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi"))
        {
            deltas.Add(d);
        }

        deltas.Should().Equal("c1", "c2", "c3");
        provider.Received.Should().BeEmpty("short-circuit filter did not call next");
        deltaObserved.Should().Equal("c1", "c2", "c3");
    }

    // ---- 2: rate-limit denial ----

    [Fact]
    public async Task Around_Provider_Filter_Denies_Propagates_Without_Yielding_Or_Retrying()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("x") });
        var denier = new DenyingStreamingFilter();

        // Fast pipeline to prove retries would have happened if the exception weren't excluded.
        var retryCount = 0;
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => !StatefulAiAgent.IsFilterDomainException(ex)),
                OnRetry = _ => { retryCount++; return ValueTask.CompletedTask; },
            })
            .Build();

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new IStreamingAgentFilter[] { denier },
            StreamingResiliencePipeline = pipeline,
        });

        var deltas = new List<string>();
        var act = async () =>
        {
            await foreach (var d in agent.StreamAsync("hi"))
            {
                deltas.Add(d);
            }
        };

        var thrown = await act.Should().ThrowAsync<AgentGuardrailDeniedException>();
        thrown.Which.Layer.Should().Be(GuardrailLayer.Input);
        deltas.Should().BeEmpty();
        retryCount.Should().Be(0, "filter-domain exceptions are not retried");
        provider.Received.Should().BeEmpty("denier threw before calling next");
    }

    // ---- 3: request rewriter ----

    [Fact]
    public async Task Around_Provider_Filter_Rewrites_Request_Before_Next()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("ok") });
        var rewriter = new RequestRewritingStreamingFilter(r => r with { MaxTokens = 42 });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new IStreamingAgentFilter[] { rewriter },
        });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        provider.Received.Should().ContainSingle();
        provider.Received[0].MaxTokens.Should().Be(42);
    }

    // ---- 4: chain composition order ----

    [Fact]
    public async Task Two_Around_Provider_Filters_Compose_In_Registration_Order()
    {
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("ok") });
        var log = new List<string>();
        var outer = new RecordingStreamingFilter(log, "outer");
        var inner = new RecordingStreamingFilter(log, "inner");
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new IStreamingAgentFilter[] { outer, inner },
        });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        log.Should().Equal("outer:before", "inner:before", "inner:after", "outer:after");
    }

    // ---- 5: filter implementing both around-provider + delta hook ----

    [Fact]
    public async Task Around_Provider_And_Delta_Hook_Coexist_On_Same_Filter()
    {
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("x"),
            new CompletionUpdate("y"),
        });
        var filter = new AroundAndDeltaFilter();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingFilters = new IStreamingAgentFilter[] { filter },
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }

        filter.InvokeAsyncCalled.Should().BeTrue();
        filter.DeltaCount.Should().Be(2);
        deltas.Should().Equal("[x]", "[y]");
    }

    // ---- 6: retry on transient failure pre-first-delta ----

    [Fact]
    public async Task Retry_On_Pre_First_Delta_Transient_Failure_Succeeds_On_Second_Attempt()
    {
        var provider = new FlakyStreamingProvider(
            attemptBehaviors: new Func<int, IAsyncEnumerable<CompletionUpdate>>[]
            {
                _ => throw new HttpRequestException("boom"),
                _ => YieldThree("a", "b", "c"),
            });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingResiliencePipeline = ZeroDelayRetryPipeline(),
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }

        deltas.Should().Equal("a", "b", "c");
        provider.Attempts.Should().Be(2);
    }

    // ---- 7: retry on first-MoveNextAsync failure ----

    [Fact]
    public async Task Retry_On_First_MoveNext_Failure_Succeeds_On_Second_Attempt()
    {
        var provider = new FlakyStreamingProvider(
            attemptBehaviors: new Func<int, IAsyncEnumerable<CompletionUpdate>>[]
            {
                _ => ThrowOnFirstMoveNext(),
                _ => YieldThree("a", "b", "c"),
            });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingResiliencePipeline = ZeroDelayRetryPipeline(),
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }

        deltas.Should().Equal("a", "b", "c");
        provider.Attempts.Should().Be(2);
    }

    // ---- 8: post-first-delta failure is NOT retried ----

    [Fact]
    public async Task Post_First_Delta_Failure_Surfaces_Without_Retry()
    {
        var bus = new Vais.Agents.Hosting.InMemory.InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var provider = new FlakyStreamingProvider(
            attemptBehaviors: new Func<int, IAsyncEnumerable<CompletionUpdate>>[]
            {
                _ => YieldThenThrow("delta-1"),
                _ => throw new InvalidOperationException("SHOULD NOT BE CALLED"),
            });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            EventBus = bus,
            StreamingResiliencePipeline = ZeroDelayRetryPipeline(),
        });

        var deltas = new List<string>();
        var act = async () =>
        {
            await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }
        };

        await act.Should().ThrowAsync<IOException>().WithMessage("mid-stream");
        deltas.Should().Equal("delta-1");
        provider.Attempts.Should().Be(1, "post-first-delta failures are committed — no retry");
        events[^1].Should().BeOfType<TurnFailed>();
    }

    // ---- 9: cancellation pre-first-delta is not retried ----

    [Fact]
    public async Task Pre_First_Delta_Cancellation_Is_Not_Retried()
    {
        var provider = new FlakyStreamingProvider(
            attemptBehaviors: new Func<int, IAsyncEnumerable<CompletionUpdate>>[]
            {
                _ => throw new OperationCanceledException(),
                _ => throw new InvalidOperationException("SHOULD NOT BE CALLED"),
            });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            StreamingResiliencePipeline = ZeroDelayRetryPipeline(),
        });

        var act = async () =>
        {
            await foreach (var _ in agent.StreamAsync("hi")) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        provider.Attempts.Should().Be(1);
    }

    // ---- 10: filter-domain exception (guardrail-denied) is not retried ----

    [Fact]
    public async Task Guardrail_Denied_From_Input_Guardrail_Is_Not_Retried()
    {
        var provider = new FlakyStreamingProvider(
            attemptBehaviors: new Func<int, IAsyncEnumerable<CompletionUpdate>>[]
            {
                _ => throw new InvalidOperationException("provider should never be invoked"),
            });

        var denyingGuardrail = new DenyingInputGuardrail();

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            InputGuardrails = new IInputGuardrail[] { denyingGuardrail },
            StreamingResiliencePipeline = ZeroDelayRetryPipeline(),
        });

        var act = async () =>
        {
            await foreach (var _ in agent.StreamAsync("hi")) { }
        };

        await act.Should().ThrowAsync<AgentGuardrailDeniedException>();
        provider.Attempts.Should().Be(0);
    }

    // ---- 11: empty stream finalises cleanly ----

    [Fact]
    public async Task Empty_Stream_Finalises_With_Empty_Assistant_Turn_And_Completes()
    {
        var bus = new Vais.Agents.Hosting.InMemory.InMemoryAgentEventBus();
        var events = new List<AgentEvent>();
        using var _ = bus.Subscribe((e, _) => { events.Add(e); return ValueTask.CompletedTask; });

        var provider = new FakeStreamingCompletionProvider(Array.Empty<CompletionUpdate>());

        CompletionResponse? observed = null;
        var observer = new CompletingObserverStreamingFilter(r => observed = r);

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            EventBus = bus,
            StreamingFilters = new IStreamingAgentFilter[] { observer },
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("hi")) { deltas.Add(d); }

        deltas.Should().BeEmpty();
        observed.Should().NotBeNull();
        observed!.Text.Should().BeEmpty();
        agent.Session.History.Should().HaveCount(2);
        agent.Session.History[1].Role.Should().Be(AgentChatRole.Assistant);
        agent.Session.History[1].Text.Should().BeEmpty();
        events.Should().ContainSingle(e => e is TurnCompleted);
    }

    // ---- 12: per-turn retry boundary inside the tool-call loop ----

    [Fact]
    public async Task Retry_Boundary_Is_Per_Turn_Inside_Tool_Call_Loop()
    {
        // Turn 1: streams a terminal tool-call.
        // Turn 2 attempt 1: throws pre-first-delta → retry.
        // Turn 2 attempt 2: streams final answer.
        var callId = "call-1";
        var dispatcher = new RecordingDispatcher(new ToolCallOutcome(callId, "weather-result"));

        var provider = new ScriptedFlakyMultiTurnProvider(
            turn1Sync: _ => YieldToolCallUpdate(callId, "get_weather", "{}"),
            turn2Attempt1: _ => throw new HttpRequestException("transient"),
            turn2Attempt2Async: _ => YieldThree("the ", "weather ", "is nice"));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            ToolCallDispatcher = dispatcher,
            StreamingResiliencePipeline = ZeroDelayRetryPipeline(),
        });

        var deltas = new List<string>();
        await foreach (var d in agent.StreamAsync("weather?")) { deltas.Add(d); }

        deltas.Should().Equal("the ", "weather ", "is nice");
        dispatcher.Invocations.Should().HaveCount(1, "tool dispatched exactly once — turn-1 dispatches must not replay on turn-2 retry");
        dispatcher.Invocations[0].CallId.Should().Be(callId);
        provider.Attempts.Should().Be(3, "turn 1 + turn 2 attempt 1 (failed) + turn 2 attempt 2 (succeeded)");
    }

    // ---- helpers ----

    private static ResiliencePipeline ZeroDelayRetryPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => !StatefulAiAgent.IsFilterDomainException(ex)),
            })
            .Build();

#pragma warning disable CS1998 // test iterators are synchronous by design
    private static async IAsyncEnumerable<CompletionUpdate> YieldThree(string a, string b, string c)
    {
        yield return new CompletionUpdate(a);
        yield return new CompletionUpdate(b);
        yield return new CompletionUpdate(c);
    }

    private static async IAsyncEnumerable<CompletionUpdate> ThrowOnFirstMoveNext()
    {
        throw new HttpRequestException("first-move-next-boom");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<CompletionUpdate> YieldThenThrow(string firstDelta)
    {
        yield return new CompletionUpdate(firstDelta);
        throw new IOException("mid-stream");
    }

    private static IEnumerable<CompletionUpdate> YieldToolCallUpdate(string callId, string toolName, string jsonArgs)
    {
        yield return new CompletionUpdate(string.Empty, ToolCalls: new[]
        {
            new ToolCallRequest(toolName, System.Text.Json.JsonDocument.Parse(jsonArgs).RootElement, callId),
        });
    }
#pragma warning restore CS1998

    private sealed class CachedStreamingFilter(IReadOnlyList<string> chunks) : IStreamingAgentFilter
    {
        public Action<CompletionUpdate>? OnDelta { get; set; }

#pragma warning disable CS1998
        public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new CompletionUpdate(chunk);
            }
            // Intentionally no `next(...)` — short-circuit.
        }
#pragma warning restore CS1998

        public ValueTask<CompletionUpdate> OnStreamDeltaAsync(CompletionUpdate update, CancellationToken cancellationToken = default)
        {
            OnDelta?.Invoke(update);
            return ValueTask.FromResult(update);
        }
    }

    private sealed class DenyingStreamingFilter : IStreamingAgentFilter
    {
#pragma warning disable CS1998
        public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            throw new AgentGuardrailDeniedException(GuardrailLayer.Input, "denied by streaming filter");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
#pragma warning restore CS1998
    }

    private sealed class RequestRewritingStreamingFilter(Func<CompletionRequest, CompletionRequest> map) : IStreamingAgentFilter
    {
        public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var update in next(map(request), cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
    }

    private sealed class RecordingStreamingFilter(List<string> log, string name) : IStreamingAgentFilter
    {
        public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            log.Add($"{name}:before");
            await foreach (var u in next(request, cancellationToken).ConfigureAwait(false))
            {
                yield return u;
            }
            log.Add($"{name}:after");
        }
    }

    private sealed class AroundAndDeltaFilter : IStreamingAgentFilter
    {
        public bool InvokeAsyncCalled { get; private set; }
        public int DeltaCount { get; private set; }

        public async IAsyncEnumerable<CompletionUpdate> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            InvokeAsyncCalled = true;
            await foreach (var u in next(request, cancellationToken).ConfigureAwait(false))
            {
                yield return u;
            }
        }

        public ValueTask<CompletionUpdate> OnStreamDeltaAsync(CompletionUpdate update, CancellationToken cancellationToken = default)
        {
            DeltaCount++;
            return ValueTask.FromResult(new CompletionUpdate($"[{update.TextDelta}]", update.ModelId, update.PromptTokens, update.CompletionTokens, update.ToolCalls));
        }
    }

    private sealed class CompletingObserverStreamingFilter(Action<CompletionResponse> observe) : IStreamingAgentFilter
    {
        public ValueTask OnStreamCompleteAsync(CompletionResponse final, CancellationToken cancellationToken = default)
        {
            observe(final);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FlakyStreamingProvider(params Func<int, IAsyncEnumerable<CompletionUpdate>>[] attemptBehaviors)
        : ICompletionProvider, IStreamingCompletionProvider
    {
        private readonly Func<int, IAsyncEnumerable<CompletionUpdate>>[] _behaviors = attemptBehaviors;
        public int Attempts { get; private set; }

        public string ProviderName => "Flaky";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            var idx = Attempts;
            Attempts++;
            if (idx >= _behaviors.Length)
            {
                throw new InvalidOperationException($"FlakyStreamingProvider exhausted attempts (have {_behaviors.Length}, requested {idx + 1})");
            }
            return _behaviors[idx](idx);
        }
    }

    private sealed class ScriptedFlakyMultiTurnProvider(
        Func<CompletionRequest, IEnumerable<CompletionUpdate>> turn1Sync,
        Func<CompletionRequest, IAsyncEnumerable<CompletionUpdate>> turn2Attempt1,
        Func<CompletionRequest, IAsyncEnumerable<CompletionUpdate>> turn2Attempt2Async)
        : ICompletionProvider, IStreamingCompletionProvider
    {
        private readonly Func<CompletionRequest, IEnumerable<CompletionUpdate>> _turn1 = turn1Sync;
        private readonly Func<CompletionRequest, IAsyncEnumerable<CompletionUpdate>> _turn2Attempt1 = turn2Attempt1;
        private readonly Func<CompletionRequest, IAsyncEnumerable<CompletionUpdate>> _turn2Attempt2 = turn2Attempt2Async;
        public int Attempts { get; private set; }

        public string ProviderName => "ScriptedFlakyMultiTurn";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Attempts switch
            {
                1 => ToAsync(_turn1(request)),
                2 => _turn2Attempt1(request),
                3 => _turn2Attempt2(request),
                _ => throw new InvalidOperationException($"ScriptedFlakyMultiTurnProvider ran out of scripts (attempt {Attempts})"),
            };
        }

#pragma warning disable CS1998
        private static async IAsyncEnumerable<CompletionUpdate> ToAsync(IEnumerable<CompletionUpdate> src)
        {
            foreach (var u in src) yield return u;
        }
#pragma warning restore CS1998
    }

    private sealed class RecordingDispatcher(ToolCallOutcome outcome) : IToolCallDispatcher
    {
        public List<ToolCallRequest> Invocations { get; } = new();

        public ValueTask<ToolCallOutcome> DispatchAsync(ToolCallRequest request, AgentContext context, CancellationToken cancellationToken)
        {
            Invocations.Add(request);
            return ValueTask.FromResult(outcome with { CallId = request.CallId });
        }
    }

    private sealed class DenyingInputGuardrail : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(GuardrailOutcome.Deny("denied"));
    }
}
