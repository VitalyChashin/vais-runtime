// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// v0.11 PR 4: Orleans-backed idempotency store. Covers the three-phase
/// lifecycle (Begin → Complete → Replay), survives-silo-restart via the shared
/// grain-storage memory provider, TTL expiry, concurrent begins (second caller
/// sees <see cref="IdempotencyBeginStatus.InFlight"/>), and mismatch detection.
/// </summary>
[Collection(OrleansClusterCollection.CollectionName)]
public sealed class OrleansIdempotencyStoreTests
{
    private readonly OrleansClusterFixture _fixture;

    public OrleansIdempotencyStoreTests(OrleansClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Begin_Complete_Replay_Round_Trip()
    {
        var store = new OrleansIdempotencyStore(_fixture.Cluster.Client);
        var key = Scope($"begin-complete-{Guid.NewGuid():N}");

        var first = await store.TryBeginAsync(key, "fp-1", default);
        first.Status.Should().Be(IdempotencyBeginStatus.New);

        var response = new CachedResponse(201, "application/json", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow);
        await store.CompleteAsync(key, response, default);

        var second = await store.TryBeginAsync(key, "fp-1", default);
        second.Status.Should().Be(IdempotencyBeginStatus.Replay);
        second.CachedResponse.Should().NotBeNull();
        second.CachedResponse!.StatusCode.Should().Be(201);
        second.CachedResponse.Body.Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task Completed_Entry_Survives_Silo_Restart()
    {
        var store = new OrleansIdempotencyStore(_fixture.Cluster.Client);
        var key = Scope($"restart-{Guid.NewGuid():N}");

        (await store.TryBeginAsync(key, "fp-r", default)).Status.Should().Be(IdempotencyBeginStatus.New);
        await store.CompleteAsync(key,
            new CachedResponse(200, "application/json", new byte[] { 9 }, DateTimeOffset.UtcNow),
            default);

        var management = _fixture.Cluster.Client.GetGrain<global::Orleans.Runtime.IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero);

        var replay = await store.TryBeginAsync(key, "fp-r", default);
        replay.Status.Should().Be(IdempotencyBeginStatus.Replay);
        replay.CachedResponse!.StatusCode.Should().Be(200);
        replay.CachedResponse.Body.Should().Equal(new byte[] { 9 });
    }

    [Fact]
    public async Task Ttl_Expiry_Returns_New_On_Next_Begin()
    {
        var store = new OrleansIdempotencyStore(_fixture.Cluster.Client, ttl: TimeSpan.FromMilliseconds(100));
        var key = Scope($"ttl-{Guid.NewGuid():N}");

        (await store.TryBeginAsync(key, "fp-ttl", default)).Status.Should().Be(IdempotencyBeginStatus.New);
        await store.CompleteAsync(key,
            new CachedResponse(200, "application/json", Array.Empty<byte>(), DateTimeOffset.UtcNow),
            default);

        // Immediately: replays.
        (await store.TryBeginAsync(key, "fp-ttl", default)).Status.Should().Be(IdempotencyBeginStatus.Replay);

        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Past TTL: treated as missing; next Begin is fresh.
        (await store.TryBeginAsync(key, "fp-ttl", default)).Status.Should().Be(IdempotencyBeginStatus.New);
    }

    [Fact]
    public async Task Concurrent_Begin_On_Same_Key_Second_Caller_Sees_InFlight()
    {
        var store = new OrleansIdempotencyStore(_fixture.Cluster.Client);
        var key = Scope($"inflight-{Guid.NewGuid():N}");

        // First caller reserves and DOES NOT complete.
        var first = await store.TryBeginAsync(key, "fp-if", default);
        first.Status.Should().Be(IdempotencyBeginStatus.New);

        // Second caller with same key observes the in-flight reservation.
        var second = await store.TryBeginAsync(key, "fp-if", default);
        second.Status.Should().Be(IdempotencyBeginStatus.InFlight);

        // Release so subsequent tests don't collide.
        await store.ReleaseAsync(key, default);
    }

    [Fact]
    public async Task Mismatched_Fingerprint_Returns_Mismatch_With_Existing()
    {
        var store = new OrleansIdempotencyStore(_fixture.Cluster.Client);
        var key = Scope($"mismatch-{Guid.NewGuid():N}");

        (await store.TryBeginAsync(key, "original-fp", default)).Status.Should().Be(IdempotencyBeginStatus.New);
        await store.CompleteAsync(key,
            new CachedResponse(201, "application/json", new byte[] { 7 }, DateTimeOffset.UtcNow),
            default);

        var mismatch = await store.TryBeginAsync(key, "different-fp", default);
        mismatch.Status.Should().Be(IdempotencyBeginStatus.Mismatch);
        mismatch.ExistingFingerprint.Should().Be("original-fp");
    }

    // ---- helpers ----

    private static IdempotencyKey Scope(string key) => new(
        TenantId: "test-tenant",
        Method: "POST",
        Path: "/v1/agents",
        Key: key);
}
