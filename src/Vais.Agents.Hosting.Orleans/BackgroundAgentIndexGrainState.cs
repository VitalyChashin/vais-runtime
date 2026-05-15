// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>Persisted state for <see cref="BackgroundAgentIndexGrain"/>.</summary>
[GenerateSerializer]
public sealed class BackgroundAgentIndexGrainState
{
    /// <summary>All background sub-run handles registered under the owning <c>parentRunId</c>.</summary>
    [Id(0)] public List<string> Handles { get; set; } = [];
}
