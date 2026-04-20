// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Policy.Opa;
using Xunit;

namespace Vais.Agents.Control.Policy.Opa.Tests;

public sealed class DecisionCacheTests
{
    [Fact]
    public void TryGet_Hit_RoundTripsStoredDecision()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        var cache = new DecisionCache(clock, TimeSpan.FromSeconds(5), maxEntries: 128);
        cache.Set("k1", PolicyDecision.Deny("blocked"));

        cache.TryGet("k1", out var decision).Should().BeTrue();
        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be("blocked");
    }

    [Fact]
    public void TryGet_Miss_ReturnsFalseWithDefault()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cache = new DecisionCache(clock, TimeSpan.FromSeconds(5), maxEntries: 128);

        cache.TryGet("absent", out var decision).Should().BeFalse();
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public void TryGet_AfterTtlExpiry_RemovesAndReturnsMiss()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        var cache = new DecisionCache(clock, TimeSpan.FromSeconds(5), maxEntries: 128);
        cache.Set("k1", PolicyDecision.Allow);
        cache.Count.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(6));

        cache.TryGet("k1", out _).Should().BeFalse();
        cache.Count.Should().Be(0);
    }

    [Fact]
    public void ZeroTtl_DisablesCaching()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        var cache = new DecisionCache(clock, TimeSpan.Zero, maxEntries: 128);

        cache.Set("k1", PolicyDecision.Allow);
        cache.Count.Should().Be(0);
        cache.TryGet("k1", out _).Should().BeFalse();
    }

    [Fact]
    public void OverflowPurge_ShedsOldestQuarter()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));
        var cache = new DecisionCache(clock, TimeSpan.FromMinutes(10), maxEntries: 8);

        for (var i = 0; i < 8; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            cache.Set($"k{i}", PolicyDecision.Allow);
        }
        cache.Count.Should().Be(8);

        // One more over the bound → 25% shed = 2 oldest (k0, k1).
        clock.Advance(TimeSpan.FromMilliseconds(1));
        cache.Set("k8", PolicyDecision.Allow);

        cache.Count.Should().BeLessThan(9);
        cache.TryGet("k0", out _).Should().BeFalse();
        cache.TryGet("k8", out _).Should().BeTrue();
    }

    [Fact]
    public void ComputeKey_SameInput_ProducesSameKey()
    {
        var a = DecisionCache.ComputeKey("""{"a":1}""");
        var b = DecisionCache.ComputeKey("""{"a":1}""");

        a.Should().Be(b);
        a.Should().HaveLength(64);
    }

    [Fact]
    public void ComputeKey_DifferentInput_ProducesDifferentKey()
    {
        DecisionCache.ComputeKey("""{"a":1}""")
            .Should().NotBe(DecisionCache.ComputeKey("""{"a":2}"""));
    }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
