// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>Persisted state for <see cref="AgentConfigGrain"/>.</summary>
[GenerateSerializer]
public sealed class AgentConfigGrainState
{
    /// <summary>Shared system prompt for every session of this agent.</summary>
    [Id(0)]
    public string? SystemPrompt { get; set; }
}
