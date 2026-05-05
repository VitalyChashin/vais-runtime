// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.AgentRunStore;

/// <summary>Lifecycle status of a standalone agent run.</summary>
public enum AgentRunStatus
{
    /// <summary>The run is currently executing.</summary>
    Running = 0,
    /// <summary>The run finished successfully.</summary>
    Completed = 1,
    /// <summary>The run terminated with an error.</summary>
    Failed = 2,
}
