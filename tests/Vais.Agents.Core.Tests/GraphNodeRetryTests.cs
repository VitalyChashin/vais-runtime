// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>Unit tests for the shared <see cref="GraphNodeRetry"/> helper (§1d node retry policy).</summary>
public sealed class GraphNodeRetryTests
{
    // Fast policy so Task.Delay is negligible in tests.
    private static GraphNodeRetryPolicy Fast(int maxAttempts) =>
        new(maxAttempts, InitialBackoffSeconds: 0.001, BackoffMultiplier: 1.0, MaxBackoffSeconds: 0.001);

    [Fact]
    public async Task NullPolicy_RunsBodyOnce()
    {
        var calls = 0;
        var result = await GraphNodeRetry.ExecuteAsync<int>(
            policy: null, runId: "r", nodeId: "n",
            body: (attempt, _) => { calls++; return ValueTask.FromResult(attempt); },
            logger: NullLogger.Instance, ct: default);

        result.Should().Be(1);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task SucceedsAfterTransientFailures()
    {
        var calls = 0;
        var result = await GraphNodeRetry.ExecuteAsync<string>(
            policy: Fast(3), runId: "r", nodeId: "n",
            body: (attempt, _) =>
            {
                calls++;
                if (attempt < 3) throw new InvalidOperationException("transient");
                return ValueTask.FromResult("ok");
            },
            logger: NullLogger.Instance, ct: default);

        result.Should().Be("ok");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExhaustsAttempts_ThrowsLastException()
    {
        var calls = 0;
        var act = async () => await GraphNodeRetry.ExecuteAsync<int>(
            policy: Fast(3), runId: "r", nodeId: "n",
            body: (_, _) => { calls++; throw new InvalidOperationException("always"); },
            logger: NullLogger.Instance, ct: default);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("always");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task NonRetryableException_PropagatesImmediately_NoRetry()
    {
        var calls = 0;
        var act = async () => await GraphNodeRetry.ExecuteAsync<int>(
            policy: Fast(3), runId: "r", nodeId: "n",
            body: (_, _) => { calls++; throw new OperationCanceledException(); },
            logger: NullLogger.Instance, ct: default);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().Be(1, "terminal-set exceptions are never retried");
    }

    [Fact]
    public void IsRetryable_ExcludesTerminalSet()
    {
        GraphNodeRetry.IsRetryable(new InvalidOperationException()).Should().BeTrue();
        GraphNodeRetry.IsRetryable(new TimeoutException()).Should().BeTrue();
        GraphNodeRetry.IsRetryable(new OperationCanceledException()).Should().BeFalse();
    }

    private sealed class ClassifiedError : Exception, IClassifiedAgentError
    {
        public string ErrorType { get; init; } = "Test";
        public bool IsTransient { get; init; }
    }

    [Fact]
    public void IsRetryable_DefersToClassification()
    {
        GraphNodeRetry.IsRetryable(new ClassifiedError { IsTransient = true }).Should().BeTrue();
        GraphNodeRetry.IsRetryable(new ClassifiedError { IsTransient = false }).Should().BeFalse();
    }

    [Fact]
    public async Task TransientClassifiedError_RetriedToCap()
    {
        var calls = 0;
        var act = async () => await GraphNodeRetry.ExecuteAsync<int>(
            policy: Fast(3), runId: "r", nodeId: "n",
            body: (_, _) => { calls++; throw new ClassifiedError { ErrorType = "Timeout", IsTransient = true }; },
            logger: NullLogger.Instance, ct: default);

        await act.Should().ThrowAsync<ClassifiedError>();
        calls.Should().Be(3, "transient classified errors retry under the policy");
    }

    [Fact]
    public async Task NonTransientClassifiedError_NotRetried_EvenWithPolicy()
    {
        var calls = 0;
        var act = async () => await GraphNodeRetry.ExecuteAsync<int>(
            policy: Fast(3), runId: "r", nodeId: "n",
            body: (_, _) => { calls++; throw new ClassifiedError { ErrorType = "InternalError", IsTransient = false }; },
            logger: NullLogger.Instance, ct: default);

        await act.Should().ThrowAsync<ClassifiedError>();
        calls.Should().Be(1, "non-transient classified errors fail the node without retry");
    }

    [Fact]
    public void ComputeBackoff_IsExponential_AndCapped()
    {
        var policy = new GraphNodeRetryPolicy(MaxAttempts: 5, InitialBackoffSeconds: 1, BackoffMultiplier: 2, MaxBackoffSeconds: 5);

        GraphNodeRetry.ComputeBackoff(policy, 1).Should().Be(TimeSpan.FromSeconds(1)); // 1 * 2^0
        GraphNodeRetry.ComputeBackoff(policy, 2).Should().Be(TimeSpan.FromSeconds(2)); // 1 * 2^1
        GraphNodeRetry.ComputeBackoff(policy, 3).Should().Be(TimeSpan.FromSeconds(4)); // 1 * 2^2
        GraphNodeRetry.ComputeBackoff(policy, 4).Should().Be(TimeSpan.FromSeconds(5)); // 8 capped to 5
    }
}
