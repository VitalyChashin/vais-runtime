// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.RunStore;

/// <summary>Lifecycle status of a pipeline run or node execution.</summary>
public enum RunStatus
{
    /// <summary>The run/node is still in progress.</summary>
    Running = 0,

    /// <summary>The run/node completed successfully.</summary>
    Completed = 1,

    /// <summary>The run/node terminated with an error.</summary>
    Failed = 2,

    /// <summary>The run was paused at an interrupt node awaiting external input.</summary>
    Interrupted = 3,
}
