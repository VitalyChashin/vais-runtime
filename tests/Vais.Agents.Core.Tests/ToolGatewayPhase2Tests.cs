// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// TG-13 — Tests for Phase 2 reference gateway plugins:
/// <see cref="ToolLoggingMiddleware"/>, <see cref="ToolOtelMiddleware"/>,
/// <see cref="ToolDenyFilterMiddleware"/>, <see cref="ToolResponseTruncationMiddleware"/>.
/// </summary>
public sealed class ToolGatewayPhase2Tests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    // ── OTel activity isolation ──────────────────────────────────────────────
    // One-time listener for the isolation source so StartActivity returns non-null.
    // Each test starts a root span from this source to get a unique TraceId;
    // CreateListener then filters captured spans to that TraceId.

    private static readonly ActivitySource _isolationSource =
        new("vais.test.tool-gateway-phase2-isolation");

    static ToolGatewayPhase2Tests()
    {
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = src => src.Name == "vais.test.tool-gateway-phase2-isolation",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        });
    }

    private static ToolGatewayContext MakeContext(string toolName = "tool", string callId = "c1")
        => new(toolName, callId, EmptyArgs, AgentContext.Empty);

    // ── TG-9: ToolLoggingMiddleware ──────────────────────────────────────────

    [Fact]
    public async Task ToolLogging_Logs_Dispatch_And_Success()
    {
        var log = new ToolRecordingLogger<ToolLoggingMiddleware>();
        var mw = new ToolLoggingMiddleware(log);
        var ctx = MakeContext("ping");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "pong"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Result.Should().Be("pong");
        log.Levels.Should().AllBeEquivalentTo(LogLevel.Debug);
        log.Messages.Should().HaveCount(2);
        log.Messages[0].Should().Contain("ping").And.Contain("c1");
        log.Messages[1].Should().Contain("succeeded").And.Contain("ping");
    }

    [Fact]
    public async Task ToolLogging_Logs_Dispatch_And_Error()
    {
        var log = new ToolRecordingLogger<ToolLoggingMiddleware>();
        var mw = new ToolLoggingMiddleware(log);
        var ctx = MakeContext("bomb");
        Func<Task<ToolCallOutcome>> next = () =>
            Task.FromResult(new ToolCallOutcome("c1", "failed", "ToolError"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().Be("ToolError");
        log.Levels.Should().AllBeEquivalentTo(LogLevel.Debug);
        log.Messages.Should().HaveCount(2);
        log.Messages[1].Should().Contain("error").And.Contain("ToolError");
    }

    [Fact]
    public async Task ToolLogging_Does_Not_Mutate_Outcome()
    {
        var log = new ToolRecordingLogger<ToolLoggingMiddleware>();
        var mw = new ToolLoggingMiddleware(log);
        var ctx = MakeContext();
        var expected = new ToolCallOutcome("c1", "original");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(expected);

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Should().Be(expected);
    }

    // ── TG-10: ToolOtelMiddleware ────────────────────────────────────────────

    [Fact]
    public async Task ToolOtel_Emits_Activity_With_Correct_Name_And_Tags()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateOtelListener(recorded);

        var mw = new ToolOtelMiddleware();
        var ctx = new ToolGatewayContext("mytool", "call-1", EmptyArgs,
            new AgentContext() { WorkspaceId = "ws-99" });
        Func<Task<ToolCallOutcome>> next = () =>
            Task.FromResult(new ToolCallOutcome("call-1", "ok"));

        await mw.InvokeAsync(ctx, next, CancellationToken.None);

        var span = recorded.Should().ContainSingle(a => a.OperationName == "tool.gateway/mytool").Subject;
        GetTag(span, AgenticTags.ToolName).Should().Be("mytool");
        GetTag(span, AgenticTags.ToolCallId).Should().Be("call-1");
        GetTag(span, AgenticTags.WorkspaceId).Should().Be("ws-99");
        span.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public async Task ToolOtel_Sets_Error_Status_When_Outcome_Has_Error()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateOtelListener(recorded);

        var mw = new ToolOtelMiddleware();
        var ctx = MakeContext("badtool");
        Func<Task<ToolCallOutcome>> next = () =>
            Task.FromResult(new ToolCallOutcome("c1", "denied", "ToolDenied"));

        await mw.InvokeAsync(ctx, next, CancellationToken.None);

        var span = recorded.Should().ContainSingle(a => a.OperationName == "tool.gateway/badtool").Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ToolOtel_Sets_Error_Status_And_Type_Tag_On_Throw()
    {
        using var root = _isolationSource.StartActivity("test-root");
        var recorded = new List<Activity>();
        using var listener = CreateOtelListener(recorded);

        var mw = new ToolOtelMiddleware();
        var ctx = MakeContext("faulty");
        Func<Task<ToolCallOutcome>> throwingNext = () =>
            throw new InvalidOperationException("infra failure");

        Func<Task> act = () => mw.InvokeAsync(ctx, throwingNext, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var span = recorded.Should().ContainSingle(a => a.OperationName == "tool.gateway/faulty").Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        GetTag(span, AgenticTags.ErrorType).Should().Be("InvalidOperationException");
    }

    [Fact]
    public async Task ToolOtel_Is_Safe_With_No_Listener()
    {
        // No ActivityListener registered for Vais.Agents → StartActivity returns null; must not throw.
        var mw = new ToolOtelMiddleware();
        var ctx = MakeContext();
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        Func<Task> act = () => mw.InvokeAsync(ctx, next, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── TG-11: ToolDenyFilterMiddleware ──────────────────────────────────────

    [Fact]
    public async Task ToolDenyFilter_Blocked_Tool_Returns_Denied_Outcome_Without_Calling_Next()
    {
        var invoked = false;
        var mw = new ToolDenyFilterMiddleware(["dangerous", "secret"]);
        var ctx = MakeContext("dangerous");
        Func<Task<ToolCallOutcome>> next = () => { invoked = true; return Task.FromResult(new ToolCallOutcome("c1", "result")); };

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        invoked.Should().BeFalse();
        outcome.Error.Should().Be("ToolDenied");
        outcome.CallId.Should().Be("c1");
        outcome.Result.Should().Contain("dangerous");
    }

    [Fact]
    public async Task ToolDenyFilter_Allowed_Tool_Passes_Through()
    {
        var mw = new ToolDenyFilterMiddleware(["dangerous"]);
        var ctx = MakeContext("safe");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().BeNull();
        outcome.Result.Should().Be("ok");
    }

    [Fact]
    public async Task ToolDenyFilter_Matching_Is_Case_Insensitive()
    {
        var invoked = false;
        var mw = new ToolDenyFilterMiddleware(["DangerousTool"]);
        var ctx = MakeContext("dangeroustool");
        Func<Task<ToolCallOutcome>> next = () => { invoked = true; return Task.FromResult(new ToolCallOutcome("c1", "ok")); };

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        invoked.Should().BeFalse();
        outcome.Error.Should().Be("ToolDenied");
    }

    // ── TG-12: ToolResponseTruncationMiddleware ───────────────────────────────

    [Fact]
    public async Task ToolTruncation_Short_Result_Is_Returned_Unchanged()
    {
        var mw = new ToolResponseTruncationMiddleware(maxCharacters: 100);
        var ctx = MakeContext();
        var original = new ToolCallOutcome("c1", "short response");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(original);

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Should().Be(original);
    }

    [Fact]
    public async Task ToolTruncation_Long_Result_Is_Truncated_With_Suffix()
    {
        const int limit = 10;
        var mw = new ToolResponseTruncationMiddleware(maxCharacters: limit);
        var ctx = MakeContext();
        var longResult = new string('x', 50);
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", longResult));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Result.Should().StartWith(new string('x', limit));
        outcome.Result.Should().Contain("[Truncated:");
        outcome.Error.Should().BeNull();
    }

    [Fact]
    public async Task ToolTruncation_Error_Outcome_Is_Never_Truncated()
    {
        const int limit = 5;
        var mw = new ToolResponseTruncationMiddleware(maxCharacters: limit);
        var ctx = MakeContext();
        var errorOutcome = new ToolCallOutcome("c1", new string('e', 100), "SomeError");
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(errorOutcome);

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Should().Be(errorOutcome);
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

    private sealed class ToolRecordingLogger<T> : ILogger<T>
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
}
