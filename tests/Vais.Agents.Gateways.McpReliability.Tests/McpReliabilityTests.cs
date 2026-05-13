// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Gateways.McpReliability.Tests;

public sealed class McpReliabilityTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static ToolGatewayContext MakeContext(string toolName = "tool", string callId = "c1")
        => new(toolName, callId, EmptyArgs, AgentContext.Empty);

    // ── ToolRetryMiddleware ──────────────────────────────────────────────────

    [Fact]
    public async Task Retry_Retries_On_Transient_Error_Until_MaxAttempts()
    {
        var attempts = 0;
        var mw = new ToolRetryMiddleware(maxAttempts: 3, initialDelay: TimeSpan.Zero);
        var ctx = MakeContext();
        Func<Task<ToolCallOutcome>> next = () =>
        {
            attempts++;
            return Task.FromResult(new ToolCallOutcome("c1", "failed", "TransientError"));
        };

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        attempts.Should().Be(3);
        outcome.Error.Should().Be("TransientError");
    }

    [Fact]
    public async Task Retry_Succeeds_On_Second_Attempt()
    {
        var attempts = 0;
        var mw = new ToolRetryMiddleware(maxAttempts: 3, initialDelay: TimeSpan.Zero);
        var ctx = MakeContext();
        Func<Task<ToolCallOutcome>> next = () =>
        {
            attempts++;
            if (attempts < 2)
                return Task.FromResult(new ToolCallOutcome("c1", "failed", "TransientError"));
            return Task.FromResult(new ToolCallOutcome("c1", "ok"));
        };

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        attempts.Should().Be(2);
        outcome.Error.Should().BeNull();
        outcome.Result.Should().Be("ok");
    }

    [Fact]
    public async Task Retry_Does_Not_Retry_ToolDenied()
    {
        var attempts = 0;
        var mw = new ToolRetryMiddleware(maxAttempts: 3, initialDelay: TimeSpan.Zero);
        var ctx = MakeContext();
        Func<Task<ToolCallOutcome>> next = () =>
        {
            attempts++;
            return Task.FromResult(new ToolCallOutcome("c1", "denied", "ToolDenied"));
        };

        await mw.InvokeAsync(ctx, next, CancellationToken.None);

        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Retry_Does_Not_Retry_CircuitOpen()
    {
        var attempts = 0;
        var mw = new ToolRetryMiddleware(maxAttempts: 3, initialDelay: TimeSpan.Zero);
        var ctx = MakeContext();
        Func<Task<ToolCallOutcome>> next = () =>
        {
            attempts++;
            return Task.FromResult(new ToolCallOutcome("c1", "open", "CircuitOpen"));
        };

        await mw.InvokeAsync(ctx, next, CancellationToken.None);

        attempts.Should().Be(1);
    }

    // ── ToolTimeoutGuard ─────────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_Returns_Timeout_Outcome_When_Next_Exceeds_Deadline()
    {
        var mw = new ToolTimeoutGuard(TimeSpan.FromMilliseconds(50));
        var ctx = MakeContext("slow");
        Func<Task<ToolCallOutcome>> next = async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return new ToolCallOutcome("c1", "never");
        };

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().Be("ToolTimeout");
        outcome.Result.Should().Contain("slow");
    }

    [Fact]
    public async Task Timeout_Passes_Through_When_Next_Completes_In_Time()
    {
        var mw = new ToolTimeoutGuard(TimeSpan.FromSeconds(10));
        var ctx = MakeContext();
        Func<Task<ToolCallOutcome>> next = () => Task.FromResult(new ToolCallOutcome("c1", "fast"));

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        outcome.Error.Should().BeNull();
        outcome.Result.Should().Be("fast");
    }

    [Fact]
    public async Task Timeout_Propagates_Outer_Cancellation()
    {
        var mw = new ToolTimeoutGuard(TimeSpan.FromSeconds(10));
        var ctx = MakeContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task<ToolCallOutcome>> next = async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
            return new ToolCallOutcome("c1", "never");
        };

        Func<Task> act = () => mw.InvokeAsync(ctx, next, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── ToolCircuitBreakerMiddleware ─────────────────────────────────────────

    [Fact]
    public async Task CircuitBreaker_Trips_After_Threshold_Failures()
    {
        var mw = new ToolCircuitBreakerMiddleware(
            failureThreshold: 3,
            resetTimeout: TimeSpan.FromMinutes(10));
        var ctx = new ToolGatewayContext("tool", "c1", EmptyArgs,
            new AgentContext() { WorkspaceId = "ws-trip" });
        Func<Task<ToolCallOutcome>> failNext = () =>
            Task.FromResult(new ToolCallOutcome("c1", "err", "SomeError"));

        // First 3 calls: fail, record failures
        for (var i = 0; i < 3; i++)
            await mw.InvokeAsync(ctx, failNext, CancellationToken.None);

        // Next call: circuit should be open
        var outcome = await mw.InvokeAsync(ctx, failNext, CancellationToken.None);

        outcome.Error.Should().Be("CircuitOpen");
    }

    [Fact]
    public async Task CircuitBreaker_Resets_After_Timeout()
    {
        var mw = new ToolCircuitBreakerMiddleware(
            failureThreshold: 2,
            resetTimeout: TimeSpan.FromMilliseconds(100));
        var ctx = new ToolGatewayContext("tool", "c1", EmptyArgs,
            new AgentContext() { WorkspaceId = "ws-reset" });
        Func<Task<ToolCallOutcome>> failNext = () =>
            Task.FromResult(new ToolCallOutcome("c1", "err", "SomeError"));
        Func<Task<ToolCallOutcome>> okNext = () =>
            Task.FromResult(new ToolCallOutcome("c1", "ok"));

        // Trip the circuit
        await mw.InvokeAsync(ctx, failNext, CancellationToken.None);
        await mw.InvokeAsync(ctx, failNext, CancellationToken.None);

        var tripped = await mw.InvokeAsync(ctx, failNext, CancellationToken.None);
        tripped.Error.Should().Be("CircuitOpen");

        // Wait for reset
        await Task.Delay(200);

        // Should pass through now (half-open: one attempt allowed)
        var recovered = await mw.InvokeAsync(ctx, okNext, CancellationToken.None);
        recovered.Error.Should().BeNull();
    }

    [Fact]
    public async Task CircuitBreaker_Does_Not_Count_ToolDenied_As_Failure()
    {
        var mw = new ToolCircuitBreakerMiddleware(
            failureThreshold: 2,
            resetTimeout: TimeSpan.FromMinutes(10));
        var ctx = new ToolGatewayContext("tool", "c1", EmptyArgs,
            new AgentContext() { WorkspaceId = "ws-denied" });
        Func<Task<ToolCallOutcome>> deniedNext = () =>
            Task.FromResult(new ToolCallOutcome("c1", "denied", "ToolDenied"));

        // 5 ToolDenied calls — circuit must not trip (threshold=2)
        for (var i = 0; i < 5; i++)
            await mw.InvokeAsync(ctx, deniedNext, CancellationToken.None);

        // Should still pass through (not open)
        Func<Task<ToolCallOutcome>> okNext = () => Task.FromResult(new ToolCallOutcome("c1", "ok"));
        var outcome = await mw.InvokeAsync(ctx, okNext, CancellationToken.None);
        outcome.Error.Should().BeNull();
    }
}
