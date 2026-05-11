// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using A2A;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-backed <see cref="ITaskStore"/> — persists A2A task state into
/// per-taskId <see cref="IA2ATaskGrain"/> grains so the A2A server's task state
/// (especially <c>input-required</c> tasks awaiting human resume) survives silo
/// restart. Pairs with <c>Vais.Agents.Protocols.A2A.Server</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>ListTasksAsync is a stub in v0.8.</b> The A2A spec's list-tasks operation is
/// secondary to the input-required use case; a full implementation requires a
/// context-index grain keyed by <c>ContextId</c> so we can answer queries without
/// scanning all grains. The <see cref="A2ATaskSurrogate"/> already denormalises
/// <see cref="A2ATaskSurrogate.ContextId"/> so a future index grain can be wired in
/// without a storage migration.
/// </para>
/// </remarks>
public sealed class OrleansTaskStore : ITaskStore
{
    private readonly IGrainFactory _grainFactory;

    /// <summary>Construct the store. <paramref name="grainFactory"/> is typically resolved via the silo's DI container or an Orleans client.</summary>
    public OrleansTaskStore(IGrainFactory grainFactory)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        _grainFactory = grainFactory;
    }

    /// <inheritdoc />
    public async Task<AgentTask?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        var grain = _grainFactory.GetGrain<IA2ATaskGrain>(taskId);
        var surrogate = await grain.GetAsync().ConfigureAwait(false);
        if (surrogate is null)
        {
            return null;
        }
        return Deserialize(surrogate.Value);
    }

    /// <inheritdoc />
    public async Task SaveTaskAsync(string taskId, AgentTask task, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        ArgumentNullException.ThrowIfNull(task);
        var grain = _grainFactory.GetGrain<IA2ATaskGrain>(taskId);
        var surrogate = new A2ATaskSurrogate
        {
            TaskId = taskId,
            ContextId = task.ContextId ?? string.Empty,
            TaskJson = JsonSerializer.Serialize(task, A2AJsonUtilities.DefaultOptions),
            SavedAt = DateTimeOffset.UtcNow,
        };
        await grain.SaveAsync(surrogate).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        var grain = _grainFactory.GetGrain<IA2ATaskGrain>(taskId);
        await grain.ClearAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// v0.8 stub — returns an empty response. Full listing requires a context-index grain.
    /// </remarks>
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ListTasksResponse { TotalSize = 0 });
    }

    internal static AgentTask Deserialize(A2ATaskSurrogate surrogate)
    {
        var task = JsonSerializer.Deserialize<AgentTask>(surrogate.TaskJson, A2AJsonUtilities.DefaultOptions);
        if (task is null)
        {
            throw new InvalidOperationException($"Stored task '{surrogate.TaskId}' deserialised to null — storage corruption?");
        }
        return task;
    }
}
