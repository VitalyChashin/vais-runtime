// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-backed <see cref="IGraphCheckpointer"/> — persists graph-run checkpoints
/// into per-runId <see cref="IGraphCheckpointGrain"/> grains so interrupted graphs
/// (and the <c>input-required</c>-analogue <c>GraphInterrupted</c> state) survive
/// silo restart. Pairs with <c>Vais.Agents.Core.InProcessGraphOrchestrator&lt;TState&gt;</c>
/// + its <c>ResumeAsync</c> path.
/// </summary>
/// <remarks>
/// <para>
/// Same split shape as v0.8's <c>InMemoryTaskStore</c> /
/// <c>OrleansTaskStore</c> pair — <c>InMemoryCheckpointer</c> for dev/tests ships in
/// <c>Vais.Agents.Core</c>; the durable Orleans-grain-backed implementation ships
/// here so the Orleans dependency stays in the hosting package.
/// </para>
/// </remarks>
public sealed class OrleansCheckpointer : IGraphCheckpointer
{
    private readonly IGrainFactory _grainFactory;

    /// <summary>Construct the checkpointer. <paramref name="grainFactory"/> is typically resolved via the silo's DI container or an Orleans client.</summary>
    public OrleansCheckpointer(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async ValueTask SaveAsync(GraphCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var grain = _grainFactory.GetGrain<IGraphCheckpointGrain>(checkpoint.RunId);
        var surrogate = new GraphCheckpointSurrogate
        {
            RunId = checkpoint.RunId,
            GraphId = checkpoint.GraphId,
            GraphVersion = checkpoint.GraphVersion,
            CheckpointJson = JsonSerializer.Serialize(checkpoint),
            IsComplete = checkpoint.IsComplete,
            SavedAt = DateTimeOffset.UtcNow,
        };
        await grain.SaveAsync(surrogate).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<GraphCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        var grain = _grainFactory.GetGrain<IGraphCheckpointGrain>(runId);
        var surrogate = await grain.GetAsync().ConfigureAwait(false);
        if (surrogate is null)
        {
            return null;
        }
        return Deserialize(surrogate.Value);
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        var grain = _grainFactory.GetGrain<IGraphCheckpointGrain>(runId);
        await grain.ClearAsync().ConfigureAwait(false);
    }

    internal static GraphCheckpoint Deserialize(GraphCheckpointSurrogate surrogate)
    {
        var checkpoint = JsonSerializer.Deserialize<GraphCheckpoint>(surrogate.CheckpointJson);
        if (checkpoint is null)
        {
            throw new InvalidOperationException(
                $"Stored checkpoint for run '{surrogate.RunId}' deserialised to null — storage corruption?");
        }
        return checkpoint;
    }
}
