// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Persisted state for <see cref="A2ATaskGrain"/>. Hosts a single
/// <see cref="A2ATaskSurrogate"/> plus a <see cref="HasTask"/> flag so the grain can
/// distinguish "never saved" from "saved and cleared" — returning null from
/// <see cref="IA2ATaskGrain.GetAsync"/> means no task exists, and the SDK's
/// <c>GetTaskAsync</c> relies on that distinction to raise <c>TaskNotFound</c>.
/// </summary>
[GenerateSerializer]
public sealed class A2ATaskGrainState
{
    /// <summary>Whether a task has been saved under this grain id.</summary>
    [Id(0)]
    public bool HasTask { get; set; }

    /// <summary>The persisted task. Meaningful only when <see cref="HasTask"/> is true.</summary>
    [Id(1)]
    public A2ATaskSurrogate Task { get; set; }
}
