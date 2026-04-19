// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-side surface for a single A2A task's durable state. Each grain instance holds
/// the serialized <c>A2A.AgentTask</c> for one task id; the wire shape is a JSON string
/// rather than the polymorphic SDK record, so we don't have to mirror every
/// <c>A2A.AgentTask</c> property in a hand-rolled surrogate as <c>A2A.AgentTask</c> evolves
/// across SDK previews.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grain key.</b> <see cref="IGrainWithStringKey"/>. The key is the opaque A2A task id
/// — globally unique within the store, no encoding needed.
/// </para>
/// <para>
/// <b>Wire type.</b> <c>A2A.AgentTask</c> carries <see cref="System.Text.Json.JsonElement"/>
/// metadata plus polymorphic <c>Part</c> content unions, which are awkward to round-trip
/// through Orleans' generated serializer. Serialising the whole task to JSON via the SDK's
/// own <c>A2AJsonUtilities.DefaultOptions</c> and storing the string keeps schema drift
/// coupled to SDK version bumps, not Orleans serializer edits.
/// </para>
/// <para>
/// <b>Single-writer guarantee.</b> Orleans serialises calls per grain = per task id here —
/// the A2A SDK only writes one `SaveTaskAsync` per turn, so contention is trivial.
/// </para>
/// </remarks>
public interface IA2ATaskGrain : IGrainWithStringKey
{
    /// <summary>Load the persisted task, or <c>null</c> if none has been saved under this id.</summary>
    Task<A2ATaskSurrogate?> GetAsync();

    /// <summary>Upsert the task's serialized representation.</summary>
    Task SaveAsync(A2ATaskSurrogate task);

    /// <summary>Clear the task's state and deactivate on idle.</summary>
    Task ClearAsync();
}

/// <summary>
/// Orleans wire-shape for a persisted A2A task — the SDK's <c>A2A.AgentTask</c> serialised
/// to JSON plus its <c>ContextId</c> pulled out as a denormalised field so the store can
/// answer list-by-context queries without deserialising every blob.
/// </summary>
[GenerateSerializer]
public struct A2ATaskSurrogate
{
    /// <summary>The A2A task id (= the grain key). Redundant with the grain key but handy for debugging.</summary>
    [Id(0)]
    public string TaskId;

    /// <summary>The task's <c>ContextId</c> — denormalised so a future context-index grain can list by context without deserialising the blob. Not indexed in v0.8 — <c>ITaskStore.ListTasksAsync</c> returns empty.</summary>
    [Id(1)]
    public string ContextId;

    /// <summary>Full <c>A2A.AgentTask</c> serialised via <c>A2A.A2AJsonUtilities.DefaultOptions</c>.</summary>
    [Id(2)]
    public string TaskJson;

    /// <summary>UTC timestamp of the most recent save — used for stale-task pruning (future work).</summary>
    [Id(3)]
    public DateTimeOffset SavedAt;
}
