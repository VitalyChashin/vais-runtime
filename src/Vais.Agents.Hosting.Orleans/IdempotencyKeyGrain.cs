// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Default <see cref="IIdempotencyKeyGrain"/> implementation. Persists under the
/// same storage name as <see cref="AiAgentGrain"/>
/// (<see cref="AiAgentGrain.StorageName"/>) so hosts register one grain storage
/// provider for the whole stack. All three lifecycle methods run on the grain
/// thread, so the atomicity that <see cref="IIdempotencyStore"/> contracts are
/// guaranteed by Orleans' single-activation scheduler.
/// </summary>
public sealed class IdempotencyKeyGrain : Grain, IIdempotencyKeyGrain
{
    private readonly IPersistentState<IdempotencyKeyGrainState> _state;

    /// <summary>Grain constructor. Dependencies resolved from silo DI.</summary>
    public IdempotencyKeyGrain(
        [PersistentState("idempotency-key", AiAgentGrain.StorageName)] IPersistentState<IdempotencyKeyGrainState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    /// <inheritdoc />
    public async Task<IdempotencyGrainBeginResult> TryBeginAsync(string fingerprint, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrEmpty(fingerprint);

        var now = DateTimeOffset.UtcNow;

        // No prior entry — reserve fresh.
        if (!_state.State.HasEntry)
        {
            return await ReserveFreshAsync(fingerprint, ttl, now);
        }

        // Prior entry — check expiry first; an expired leftover is treated as missing.
        var entry = _state.State.Entry;
        if (entry.ExpiresAt <= now)
        {
            return await ReserveFreshAsync(fingerprint, ttl, now);
        }

        // Live entry — classify by state + fingerprint.
        if (entry.State == IdempotencyEntryState.InFlight)
        {
            return new IdempotencyGrainBeginResult { Status = IdempotencyBeginStatus.InFlight };
        }

        // Completed.
        if (string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new IdempotencyGrainBeginResult
            {
                Status = IdempotencyBeginStatus.Replay,
                CachedResponseJson = entry.ResponseJson,
            };
        }

        return new IdempotencyGrainBeginResult
        {
            Status = IdempotencyBeginStatus.Mismatch,
            ExistingFingerprint = entry.Fingerprint,
        };
    }

    /// <inheritdoc />
    public async Task CompleteAsync(string responseJson, TimeSpan ttl, DateTimeOffset completedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(responseJson);

        // Preserve fingerprint from the existing reservation — caller doesn't need to
        // re-pass it; the grain already owns it from TryBeginAsync. If the entry was
        // evicted (TTL set too low), this silently no-ops to avoid writing a
        // Completed entry with an empty fingerprint that would fail all future match checks.
        if (!_state.State.HasEntry)
        {
            return;
        }
        var fingerprint = _state.State.Entry.Fingerprint;
        _state.State.Entry = new IdempotencyKeySurrogate
        {
            GrainKey = this.GetPrimaryKeyString(),
            State = IdempotencyEntryState.Completed,
            Fingerprint = fingerprint,
            ResponseJson = responseJson,
            ExpiresAt = completedAt + ttl,
        };
        await _state.WriteStateAsync();
    }

    /// <inheritdoc />
    public async Task ReleaseAsync()
    {
        await _state.ClearStateAsync();
        _state.State.HasEntry = false;
        _state.State.Entry = default;
        DeactivateOnIdle();
    }

    private async Task<IdempotencyGrainBeginResult> ReserveFreshAsync(string fingerprint, TimeSpan ttl, DateTimeOffset now)
    {
        _state.State.HasEntry = true;
        _state.State.Entry = new IdempotencyKeySurrogate
        {
            GrainKey = this.GetPrimaryKeyString(),
            State = IdempotencyEntryState.InFlight,
            Fingerprint = fingerprint,
            ResponseJson = null,
            ExpiresAt = now + ttl,
        };
        await _state.WriteStateAsync();
        return new IdempotencyGrainBeginResult { Status = IdempotencyBeginStatus.New };
    }
}
