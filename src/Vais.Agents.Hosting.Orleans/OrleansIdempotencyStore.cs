// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Vais.Agents.Control;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-backed <see cref="IIdempotencyStore"/>. Routes the three-phase
/// lifecycle through a per-scope <see cref="IIdempotencyKeyGrain"/> whose state
/// persists via the silo's grain-storage provider (reusing the
/// <see cref="AiAgentGrain.StorageName"/> name, same as v0.8/v0.9).
/// </summary>
/// <remarks>
/// <para>
/// Same two-tier split as v0.8's <c>InMemoryTaskStore</c> /
/// <c>OrleansTaskStore</c> + v0.9's <c>InMemoryCheckpointer</c> /
/// <c>OrleansCheckpointer</c> — pick one. Hosts wire this via
/// <see cref="AgenticHostingOrleansServiceCollectionExtensions.AddOrleansIdempotencyStore"/>
/// before the Control.Http.Server's <c>AddAgentControlPlaneIdempotency</c> so
/// the <c>TryAddSingleton</c> discipline picks the Orleans implementation.
/// </para>
/// </remarks>
public sealed class OrleansIdempotencyStore : IIdempotencyStore
{
    /// <summary>Default TTL used when a consumer doesn't override via ctor. Matches Stripe's published retention window.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private readonly IGrainFactory _grainFactory;
    private readonly TimeSpan _ttl;

    /// <summary>Construct the store with the default 24h TTL.</summary>
    public OrleansIdempotencyStore(IGrainFactory grainFactory)
        : this(grainFactory, DefaultTtl)
    {
    }

    /// <summary>
    /// Construct the store with an explicit TTL. Consumers who care about the
    /// TTL matching their HTTP-side <c>IdempotencyOptions.Ttl</c> pass the same
    /// value here.
    /// </summary>
    public OrleansIdempotencyStore(IGrainFactory grainFactory, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        if (ttl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Idempotency TTL must be positive.");
        }
        _grainFactory = grainFactory;
        _ttl = ttl;
    }

    /// <inheritdoc />
    public async ValueTask<IdempotencyBeginResult> TryBeginAsync(
        IdempotencyKey key,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var grain = GetGrain(key);
        var grainResult = await grain.TryBeginAsync(fingerprint, _ttl).ConfigureAwait(false);
        CachedResponse? cached = null;
        if (grainResult.CachedResponseJson is { Length: > 0 } json)
        {
            cached = JsonSerializer.Deserialize<CachedResponse>(json);
        }
        return new IdempotencyBeginResult(grainResult.Status, cached, grainResult.ExistingFingerprint);
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(IdempotencyKey key, CachedResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var grain = GetGrain(key);
        var responseJson = JsonSerializer.Serialize(response);
        // Grain preserves fingerprint from its prior TryBeginAsync state; we only
        // supply the response + TTL knobs. No extra round-trip.
        await grain.CompleteAsync(responseJson, _ttl, response.CompletedAt).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        var grain = GetGrain(key);
        await grain.ReleaseAsync().ConfigureAwait(false);
    }

    private IIdempotencyKeyGrain GetGrain(IdempotencyKey key) =>
        _grainFactory.GetGrain<IIdempotencyKeyGrain>(BuildGrainKey(key));

    /// <summary>
    /// Build the grain key string from a 4-tuple scope. Each component is
    /// URL-encoded so no component can collide with the <c>|</c> delimiter.
    /// </summary>
    public static string BuildGrainKey(IdempotencyKey key)
    {
        var tenant = string.IsNullOrEmpty(key.TenantId) ? "__anon" : Uri.EscapeDataString(key.TenantId);
        var method = Uri.EscapeDataString(key.Method);
        var path = Uri.EscapeDataString(key.Path);
        var keyPart = Uri.EscapeDataString(key.Key);
        return $"{tenant}|{method}|{path}|{keyPart}";
    }
}
