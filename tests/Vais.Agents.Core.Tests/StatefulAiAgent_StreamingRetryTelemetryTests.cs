// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0 See LICENSE in the project root for license information.

using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Verifies per-attempt telemetry on the streaming pipeline. Each retry attempt
/// in Phase 1 (enumerator-open + first MoveNextAsync) emits a child "stream_attempt"
/// span under the parent "chat" span with attempt index and status tracking.
/// </summary>
public sealed class StatefulAiAgent_StreamingRetryTelemetryTests
{
    // One-time setup: register a listener so _isolationSource.StartActivity returns non-null.
    // Each test starts a root span from this source to seed a unique TraceId; CreateListener
    // then filters captured activities to that TraceId, preventing bleed-in from parallel tests.
    private static readonly ActivitySource _isolationSource = new("vais.test.streaming-retry-isolation");

    static StatefulAiAgent_StreamingRetryTelemetryTests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = src => src.Name == "vais.test.streaming-retry-isolation",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    [Fact]
    public async Task SingleAttempt_EmitsOneAttemptSpan()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateListener(recorded);

        var provider = new RetryCountingFakeStreamingProvider(
            failBeforeAttempt: 0,
            deltas: new[] { new CompletionUpdate("Hello") });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { AgentName = "test-agent" });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        var chatSpans = recorded.Where(a => a.OperationName == "chat").ToList();
        var attemptSpans = recorded.Where(a => a.OperationName == "stream_attempt").ToList();

        chatSpans.Should().ContainSingle("should emit one parent chat span");
        attemptSpans.Should().ContainSingle("should emit one attempt span");

        var attempt = attemptSpans[0];
        attempt.ParentSpanId.Should().Be(chatSpans[0].SpanId);
        attempt.Status.Should().Be(ActivityStatusCode.Ok);
        GetTagValue(attempt, AgenticTags.StreamAttemptIndex).Should().Be(0);
    }

    [Fact]
    public async Task RetryOnce_EmitsTwoAttemptSpans()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateListener(recorded);

        var provider = new RetryCountingFakeStreamingProvider(
            failBeforeAttempt: 1,
            deltas: new[] { new CompletionUpdate("Hello") });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { AgentName = "test-agent" });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        var chatSpans = recorded.Where(a => a.OperationName == "chat").ToList();
        var attemptSpans = recorded.Where(a => a.OperationName == "stream_attempt").ToList();

        chatSpans.Should().ContainSingle();
        attemptSpans.Should().HaveCount(2);

        attemptSpans[0].ParentSpanId.Should().Be(chatSpans[0].SpanId);
        attemptSpans[0].Status.Should().Be(ActivityStatusCode.Error);
        GetTagValue(attemptSpans[0], AgenticTags.StreamAttemptIndex).Should().Be(0);
        GetTagValue(attemptSpans[0], AgenticTags.ErrorType).Should().Be("InvalidOperationException");

        attemptSpans[1].ParentSpanId.Should().Be(chatSpans[0].SpanId);
        attemptSpans[1].Status.Should().Be(ActivityStatusCode.Ok);
        GetTagValue(attemptSpans[1], AgenticTags.StreamAttemptIndex).Should().Be(1);

        chatSpans[0].Status.Should().Be(ActivityStatusCode.Ok, "parent span reflects overall success");
    }

    [Fact]
    public async Task RetryTwice_EmitsThreeAttemptSpans()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateListener(recorded);

        var provider = new RetryCountingFakeStreamingProvider(
            failBeforeAttempt: 2,
            deltas: new[] { new CompletionUpdate("Hello") });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { AgentName = "test-agent" });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        var chatSpans = recorded.Where(a => a.OperationName == "chat").ToList();
        var attemptSpans = recorded.Where(a => a.OperationName == "stream_attempt").ToList();

        chatSpans.Should().ContainSingle();
        attemptSpans.Should().HaveCount(3);

        for (int i = 0; i < 2; i++)
        {
            attemptSpans[i].Status.Should().Be(ActivityStatusCode.Error);
            GetTagValue(attemptSpans[i], AgenticTags.StreamAttemptIndex).Should().Be(i);
            GetTagValue(attemptSpans[i], AgenticTags.ErrorType).Should().Be("InvalidOperationException");
        }

        attemptSpans[2].Status.Should().Be(ActivityStatusCode.Ok);
        GetTagValue(attemptSpans[2], AgenticTags.StreamAttemptIndex).Should().Be(2);

        chatSpans[0].Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public async Task AllRetriesExhausted_ParentSpanError_ChildSpansAllError()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateListener(recorded);

        var provider = new RetryCountingFakeStreamingProvider(
            failBeforeAttempt: 999, // Always fail
            deltas: Array.Empty<CompletionUpdate>());

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { AgentName = "test-agent" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.StreamAsync("hi").GetAsyncEnumerator().MoveNextAsync().AsTask());

        var chatSpans = recorded.Where(a => a.OperationName == "chat").ToList();
        var attemptSpans = recorded.Where(a => a.OperationName == "stream_attempt").ToList();

        chatSpans.Should().ContainSingle();
        attemptSpans.Should().HaveCount(3); // 1 initial + 2 retries = 3 total

        chatSpans[0].Status.Should().Be(ActivityStatusCode.Error);
        GetTagValue(chatSpans[0], AgenticTags.ErrorType).Should().Be("InvalidOperationException");

        foreach (var attempt in attemptSpans)
        {
            attempt.Status.Should().Be(ActivityStatusCode.Error);
            attempt.ParentSpanId.Should().Be(chatSpans[0].SpanId);
            GetTagValue(attempt, AgenticTags.ErrorType).Should().Be("InvalidOperationException");
        }
    }

    [Fact]
    public async Task FilterDomainException_NoChildSpan_ImmediatelyFails()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateListener(recorded);

        var provider = new RetryCountingFakeStreamingProvider(
            failBeforeAttempt: 0,
            deltas: new[] { new CompletionUpdate("Hello") });

        var guardrail = new ThrowingGuardrail();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            AgentName = "test-agent",
            InputGuardrails = new[] { guardrail }
        });

        await Assert.ThrowsAsync<AgentGuardrailDeniedException>(() =>
            agent.StreamAsync("hi").GetAsyncEnumerator().MoveNextAsync().AsTask());

        var chatSpans = recorded.Where(a => a.OperationName == "chat").ToList();
        var attemptSpans = recorded.Where(a => a.OperationName == "stream_attempt").ToList();

        chatSpans.Should().ContainSingle();
        chatSpans[0].Status.Should().Be(ActivityStatusCode.Error);
        GetTagValue(chatSpans[0], AgenticTags.ErrorType).Should().Be("AgentGuardrailDeniedException");

        attemptSpans.Should().BeEmpty("filter-domain exceptions bypass retry, so no attempt spans");
    }

    // Filters captured spans to the TraceId of the current activity (set by the test's root
    // isolation span) so parallel-test spans don't bleed into this test's recorded list.
    private static ActivityListener CreateListener(List<Activity> sink)
    {
        var traceId = Activity.Current?.TraceId;
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == AgenticDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = traceId is null
                ? sink.Add
                : a => { if (a.TraceId == traceId) sink.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static object? GetTagValue(Activity activity, string key) =>
        activity.TagObjects.SingleOrDefault(kv => kv.Key == key).Value;

    /// <summary>
    /// Streaming provider that throws on the first N attempts, then yields deltas.
    /// Used to test retry behavior without network calls.
    /// </summary>
    private sealed class RetryCountingFakeStreamingProvider : IStreamingCompletionProvider, ICompletionProvider
    {
        private readonly int _failBeforeAttempt;
        private readonly CompletionUpdate[] _deltas;
        private int _attemptCount;

        public RetryCountingFakeStreamingProvider(int failBeforeAttempt, CompletionUpdate[] deltas)
        {
            _failBeforeAttempt = failBeforeAttempt;
            _deltas = deltas;
        }

        public List<CompletionRequest> Received { get; } = new();
        public string ProviderName => "RetryFakeStreaming";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
        {
            Received.Add(request);
            var joined = string.Concat(_deltas.Select(u => u.TextDelta));
            return Task.FromResult(new CompletionResponse(joined, "fake-stream-model"));
        }

#pragma warning disable CS1998 // Async method lacks 'await' — iterator is synchronous by design.
        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
            CompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
#pragma warning restore CS1998
        {
            Received.Add(request);
            var attempt = System.Threading.Interlocked.Increment(ref _attemptCount) - 1;

            if (attempt < _failBeforeAttempt)
            {
                throw new InvalidOperationException($"Simulated transient failure on attempt {attempt}");
            }

            foreach (var delta in _deltas)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return delta;
            }
        }
    }

    /// <summary>
    /// Guardrail that always denies with a GuardrailDeniedException.
    /// Filter-domain exceptions are excluded from retry by design.
    /// </summary>
    private sealed class ThrowingGuardrail : IInputGuardrail
    {
        public ValueTask<GuardrailOutcome> EvaluateAsync(CompletionRequest request, AgentContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GuardrailOutcome.Deny("Test guardrail denial"));
        }
    }
}
