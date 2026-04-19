// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-side surface for a single graph run's durable checkpoint. Each grain
/// instance holds one serialised <see cref="GraphCheckpoint"/> for a given run id;
/// the wire shape is a JSON string rather than a hand-rolled surrogate so
/// <see cref="GraphCheckpoint"/> evolution rides System.Text.Json's serialisation
/// contract, not a shadow Orleans struct.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grain key.</b> <see cref="IGrainWithStringKey"/>. The key is the graph run id
/// stamped in <see cref="AgentGraphEvent.RunId"/> — globally unique within the
/// cluster, no encoding needed.
/// </para>
/// <para>
/// <b>Wire type.</b> <see cref="GraphCheckpoint.State"/> carries arbitrary
/// <see cref="System.Text.Json.JsonElement"/> values (arbitrary per-consumer shapes),
/// awkward to round-trip through Orleans' generated serializer without surprises.
/// Serialising the whole checkpoint to JSON keeps schema drift coupled to
/// <see cref="GraphCheckpoint"/>'s record layout, not hand-synced Orleans edits.
/// Same pattern as v0.8's <c>A2ATaskSurrogate.TaskJson</c>.
/// </para>
/// <para>
/// <b>Single-writer guarantee.</b> Orleans serialises calls per grain = per run id —
/// the orchestrator writes one checkpoint per super-step, no contention.
/// </para>
/// </remarks>
public interface IGraphCheckpointGrain : IGrainWithStringKey
{
    /// <summary>Load the persisted checkpoint, or <c>null</c> if none has been saved under this run id.</summary>
    Task<GraphCheckpointSurrogate?> GetAsync();

    /// <summary>Upsert the checkpoint's serialized representation.</summary>
    Task SaveAsync(GraphCheckpointSurrogate checkpoint);

    /// <summary>Clear the checkpoint and deactivate on idle.</summary>
    Task ClearAsync();
}

/// <summary>
/// Orleans wire-shape for a persisted graph checkpoint — the full
/// <see cref="GraphCheckpoint"/> serialised to JSON plus a few denormalised fields
/// (<see cref="RunId"/>, <see cref="GraphId"/>, <see cref="GraphVersion"/>,
/// <see cref="IsComplete"/>, <see cref="SavedAt"/>) for audit / future indexing
/// without having to deserialise the blob.
/// </summary>
[GenerateSerializer]
public struct GraphCheckpointSurrogate
{
    /// <summary>Graph run id (= the grain key). Redundant with the key but handy for debugging.</summary>
    [Id(0)]
    public string RunId;

    /// <summary>Id of the <see cref="AgentGraphManifest"/> the checkpoint belongs to. Denormalised for audit.</summary>
    [Id(1)]
    public string GraphId;

    /// <summary>Version of the manifest. Denormalised for resume compatibility checks.</summary>
    [Id(2)]
    public string GraphVersion;

    /// <summary>Full <see cref="GraphCheckpoint"/> serialised via <see cref="System.Text.Json.JsonSerializer"/>.</summary>
    [Id(3)]
    public string CheckpointJson;

    /// <summary>True when the checkpoint marks a completed graph. Denormalised for retention-pruning queries.</summary>
    [Id(4)]
    public bool IsComplete;

    /// <summary>UTC timestamp of the most recent save.</summary>
    [Id(5)]
    public DateTimeOffset SavedAt;
}
