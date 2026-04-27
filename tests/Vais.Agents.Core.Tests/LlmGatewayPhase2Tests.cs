// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// GW-10 — Tests for Phase 2 reference gateway plugins:
/// <see cref="LlmLoggingMiddleware"/>, <see cref="LlmUsageMiddleware"/>,
/// <see cref="LlmOtelMiddleware"/>, <see cref="LlmPromptEnrichmentMiddleware"/>.
/// </summary>
public sealed class LlmGatewayPhase2Tests
{
    // ── OTel activity isolation ──────────────────────────────────────────────
    // One-time listener for the isolation source so StartActivity returns non-null.
    // Each test starts a root span from this source to get a unique TraceId;
    // CreateListener then filters captured spans to that TraceId.

    private static readonly ActivitySource _isolationSource =
        new("vais.test.gateway-phase2-isolation");

    static LlmGatewayPhase2Tests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = src => src.Name == "vais.test.gateway-phase2-isolation",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    // ── GW-6: LlmLoggingMiddleware ───────────────────────────────────────────

    [Fact]
    public async Task LlmLoggingMiddleware_Logs_Request_And_Response_On_NonStreaming_Path()
    {
        var log = new RecordingLogger<LlmLoggingMiddleware>();
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("hi", PromptTokens: 5, CompletionTokens: 3));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmLoggingMiddleware(log)],
        });

        await agent.AskAsync("hello");

        log.Messages.Should().HaveCount(2);
        log.Levels.Should().AllBeEquivalentTo(LogLevel.Debug);
        log.Messages[0].Should().Contain("turns");
        log.Messages[1].Should().Contain("5").And.Contain("3");
    }

    [Fact]
    public async Task LlmLoggingMiddleware_Logs_Stream_Start_And_Complete()
    {
        var log = new RecordingLogger<LlmLoggingMiddleware>();
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("a", PromptTokens: 4, CompletionTokens: 2),
        });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmLoggingMiddleware(log)],
        });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        log.Messages.Should().HaveCount(2);
        log.Messages[0].Should().Contain("stream start");
        log.Messages[1].Should().Contain("stream complete");
    }

    [Fact]
    public async Task LlmLoggingMiddleware_Does_Not_Mutate_Request_Or_Response()
    {
        var log = new RecordingLogger<LlmLoggingMiddleware>();
        CompletionRequest? received = null;
        var provider = new FakeCompletionProvider(r =>
        {
            received = r;
            return new CompletionResponse("original");
        });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmLoggingMiddleware(log)],
            SystemPrompt = "sys",
        });

        var result = await agent.AskAsync("hello");

        result.Should().Be("original");
        received!.SystemPrompt.Should().Be("sys");
    }

    // ── GW-7: LlmUsageMiddleware ─────────────────────────────────────────────

    [Fact]
    public async Task LlmUsageMiddleware_Reports_Once_Per_NonStreaming_Turn()
    {
        var sink = new RecordingUsageSink2();
        var accessor = new AsyncLocalAgentContextAccessor();
        var provider = new FakeCompletionProvider(_ =>
            new CompletionResponse("ok", ModelId: "gpt-x", PromptTokens: 10, CompletionTokens: 7));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmUsageMiddleware(sink, accessor)],
            // Disable the built-in usage sink to isolate the middleware's reporting.
            UsageSink = NullUsageSink.Instance,
        });

        await agent.AskAsync("hello");

        sink.Records.Should().ContainSingle();
        var r = sink.Records[0];
        r.ModelId.Should().Be("gpt-x");
        r.PromptTokens.Should().Be(10);
        r.CompletionTokens.Should().Be(7);
        r.Succeeded.Should().BeTrue();
        r.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task LlmUsageMiddleware_Reports_Once_Per_Stream_Completion()
    {
        var sink = new RecordingUsageSink2();
        var accessor = new AsyncLocalAgentContextAccessor();
        var provider = new FakeStreamingCompletionProvider(new[]
        {
            new CompletionUpdate("part1 ", ModelId: "stream-model", PromptTokens: 8),
            new CompletionUpdate("part2", CompletionTokens: 4),
        });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmUsageMiddleware(sink, accessor)],
            UsageSink = NullUsageSink.Instance,
        });

        await foreach (var _ in agent.StreamAsync("hi")) { }

        sink.Records.Should().ContainSingle();
        var r = sink.Records[0];
        r.ModelId.Should().Be("stream-model");
        r.PromptTokens.Should().Be(8);
        r.CompletionTokens.Should().Be(4);
    }

    [Fact]
    public async Task LlmUsageMiddleware_Propagates_WorkspaceId_From_Context()
    {
        var sink = new RecordingUsageSink2();
        var accessor = new AsyncLocalAgentContextAccessor();
        var provider = new FakeCompletionProvider();

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmUsageMiddleware(sink, accessor)],
            ContextAccessor = accessor,
            UsageSink = NullUsageSink.Instance,
        });

        using var _ = accessor.Push(new AgentContext() { WorkspaceId = "ws-42" });
        await agent.AskAsync("hello");

        sink.Records.Should().ContainSingle();
        sink.Records[0].WorkspaceId.Should().Be("ws-42");
    }

    // ── GW-8: LlmOtelMiddleware ──────────────────────────────────────────────

    [Fact]
    public async Task LlmOtelMiddleware_Emits_Completion_Activity_With_Model_Tag()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateOtelListener(recorded);

        var accessor = new AsyncLocalAgentContextAccessor();
        var provider = new FakeCompletionProvider(_ =>
            new CompletionResponse("ok", ModelId: "gpt-4o"));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmOtelMiddleware(accessor)],
        });

        await agent.AskAsync("hello");

        var spans = recorded.Where(a => a.OperationName == "llm.completion").ToList();
        spans.Should().ContainSingle();
        GetTag(spans[0], AgenticTags.GenAiResponseModel).Should().Be("gpt-4o");
        spans[0].Status.Should().NotBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task LlmOtelMiddleware_Sets_Error_Status_On_Provider_Exception()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateOtelListener(recorded);

        var accessor = new AsyncLocalAgentContextAccessor();
        var provider = new FakeCompletionProvider(_ => throw new InvalidOperationException("boom"));

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmOtelMiddleware(accessor)],
            ResiliencePipeline = Polly.ResiliencePipeline.Empty, // no retries
        });

        Func<Task> act = () => agent.AskAsync("hello");
        await act.Should().ThrowAsync<InvalidOperationException>();

        var spans = recorded.Where(a => a.OperationName == "llm.completion").ToList();
        spans.Should().ContainSingle();
        spans[0].Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task LlmOtelMiddleware_Emits_Stream_Activity()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateOtelListener(recorded);

        var accessor = new AsyncLocalAgentContextAccessor();
        var provider = new FakeStreamingCompletionProvider(new[] { new CompletionUpdate("hi") });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmOtelMiddleware(accessor)],
        });

        await foreach (var _ in agent.StreamAsync("hello")) { }

        var spans = recorded.Where(a => a.OperationName == "llm.completion.stream").ToList();
        spans.Should().ContainSingle();
    }

    [Fact]
    public async Task LlmOtelMiddleware_Is_Safe_With_No_Listener()
    {
        // No ActivityListener registered for the Vais.Agents source → StartActivity returns null.
        // The middleware must not throw.
        var accessor = new AsyncLocalAgentContextAccessor();
        var provider = new FakeCompletionProvider();
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmOtelMiddleware(accessor)],
        });

        Func<Task> act = () => agent.AskAsync("hello");
        await act.Should().NotThrowAsync();
    }

    // ── GW-9: LlmPromptEnrichmentMiddleware ─────────────────────────────────

    [Fact]
    public async Task PromptEnrichment_Appends_Suffix_On_NonStreaming_Path()
    {
        CompletionRequest? received = null;
        var provider = new FakeCompletionProvider(r => { received = r; return new CompletionResponse("ok"); });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmPromptEnrichmentMiddleware(suffix: "\nSAFETY")],
            SystemPrompt = "base",
        });

        await agent.AskAsync("hello");

        received!.SystemPrompt.Should().Be("base\nSAFETY");
    }

    [Fact]
    public async Task PromptEnrichment_Prepends_Prefix_On_Streaming_Path()
    {
        CompletionRequest? received = null;
        var provider = new FakeStreamingCompletionProvider(r =>
        {
            received = r;
            return [new CompletionUpdate("ok")];
        });
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            GatewayMiddleware = [new LlmPromptEnrichmentMiddleware(prefix: "PLATFORM: ")],
            SystemPrompt = "base",
        });

        await foreach (var _ in agent.StreamAsync("hello")) { }

        received!.SystemPrompt.Should().Be("PLATFORM: base");
    }

    [Fact]
    public async Task PromptEnrichment_Empty_Prefix_And_Suffix_Returns_Same_Request_Object()
    {
        var mw = new LlmPromptEnrichmentMiddleware();
        var request = new CompletionRequest([], SystemPrompt: "original");
        CompletionRequest? forwarded = null;

        IAgentFilter filter = mw;
        await filter.InvokeAsync(
            request,
            (r, _) => { forwarded = r; return Task.FromResult(new CompletionResponse("ok")); },
            CancellationToken.None);

        forwarded.Should().BeSameAs(request);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ActivityListener CreateOtelListener(List<Activity> sink)
    {
        var rootTraceId = Activity.Current?.TraceId ?? default;
        var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == AgenticDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (rootTraceId == default || a.TraceId == rootTraceId)
                    sink.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static object? GetTag(Activity activity, string key)
        => activity.Tags.FirstOrDefault(t => t.Key == key).Value;

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class RecordingUsageSink2 : IUsageSink
    {
        public List<UsageRecord> Records { get; } = [];

        public ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}
