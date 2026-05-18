// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// CS-11: Deterministic-hash sampling contract.
/// </summary>
public sealed class ProductionSamplerTests
{
    [Fact]
    public void ShouldSample_IsDeterministic_SameRunIdSameRate()
    {
        var runId = "run-abc123";
        var r1 = ProductionSampler.ShouldSample(runId, 0.5);
        var r2 = ProductionSampler.ShouldSample(runId, 0.5);
        r1.Should().Be(r2);
    }

    [Fact]
    public void ShouldSample_AtRateZero_AlwaysReturnsFalse()
    {
        for (var i = 0; i < 100; i++)
            ProductionSampler.ShouldSample(Guid.NewGuid().ToString(), 0.0).Should().BeFalse();
    }

    [Fact]
    public void ShouldSample_AtRateOne_AlwaysReturnsTrue()
    {
        for (var i = 0; i < 100; i++)
            ProductionSampler.ShouldSample(Guid.NewGuid().ToString(), 1.0).Should().BeTrue();
    }

    [Fact]
    public void ShouldSample_AtRate0_5_EmpiricalRateWithinFivePercent()
    {
        const int N = 10_000;
        var sampled = 0;
        for (var i = 0; i < N; i++)
        {
            if (ProductionSampler.ShouldSample($"run-{i:D6}", 0.5))
                sampled++;
        }
        var empiricalRate = (double)sampled / N;
        empiricalRate.Should().BeInRange(0.45, 0.55, because: "empirical rate at rate=0.5 should be within ±5%");
    }

    [Fact]
    public void ShouldSample_DifferentRunIds_ProduceDifferentResults()
    {
        var ids = Enumerable.Range(0, 20).Select(i => Guid.NewGuid().ToString()).ToList();
        var results = ids.Select(id => ProductionSampler.ShouldSample(id, 0.5)).ToList();
        results.Should().Contain(true, because: "some runs should be sampled at rate=0.5");
        results.Should().Contain(false, because: "some runs should not be sampled at rate=0.5");
    }
}
