// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Production <see cref="IBackgroundAgentTracker"/> backed by durable Orleans grains.
/// Each sub-run is owned by a <see cref="IBackgroundAgentRunGrain"/> keyed by the
/// child session id (handle); the <see cref="IBackgroundAgentIndexGrain"/> keyed by
/// <c>parentRunId</c> provides cluster-wide <c>ListAsync</c>.
/// </summary>
public sealed class OrleansBackgroundAgentTracker : IBackgroundAgentTracker
{
    private readonly IGrainFactory _grainFactory;

    /// <summary>Creates a tracker over the given grain factory.</summary>
    public OrleansBackgroundAgentTracker(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async ValueTask<string> StartAsync(
        string parentRunId,
        string childAgentId,
        string childSessionId,
        string message,
        AgentContext childContext,
        CancellationToken ct = default)
    {
        var grain = _grainFactory.GetGrain<IBackgroundAgentRunGrain>(childSessionId);
        return await grain.StartAsync(parentRunId, childAgentId, message, childContext);
    }

    /// <inheritdoc />
    public async ValueTask<BackgroundAgentRunRecord?> GetAsync(string handle, CancellationToken ct = default)
    {
        var grain = _grainFactory.GetGrain<IBackgroundAgentRunGrain>(handle);
        return await grain.GetAsync();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<BackgroundAgentRunRecord>> ListAsync(string parentRunId, CancellationToken ct = default)
    {
        var index = _grainFactory.GetGrain<IBackgroundAgentIndexGrain>(parentRunId);
        var handles = await index.ListHandlesAsync();

        var tasks = handles.Select(h => _grainFactory.GetGrain<IBackgroundAgentRunGrain>(h).GetAsync());
        var records = await Task.WhenAll(tasks);

        return records
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
    }

    /// <inheritdoc />
    public async ValueTask<bool> CancelAsync(string handle, CancellationToken ct = default)
    {
        var grain = _grainFactory.GetGrain<IBackgroundAgentRunGrain>(handle);
        return await grain.CancelAsync();
    }
}
