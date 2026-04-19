// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Persisted state for <see cref="GraphCheckpointGrain"/>. Hosts a single
/// <see cref="GraphCheckpointSurrogate"/> plus a <see cref="HasCheckpoint"/> flag
/// so the grain can distinguish "never saved" from "saved and cleared" — returning
/// null from <see cref="IGraphCheckpointGrain.GetAsync"/> means no checkpoint
/// exists, matching the v0.8 <c>A2ATaskGrainState</c> convention.
/// </summary>
[GenerateSerializer]
public sealed class GraphCheckpointGrainState
{
    /// <summary>Whether a checkpoint has been saved under this grain id.</summary>
    [Id(0)]
    public bool HasCheckpoint { get; set; }

    /// <summary>The persisted checkpoint. Meaningful only when <see cref="HasCheckpoint"/> is true.</summary>
    [Id(1)]
    public GraphCheckpointSurrogate Checkpoint { get; set; }
}
