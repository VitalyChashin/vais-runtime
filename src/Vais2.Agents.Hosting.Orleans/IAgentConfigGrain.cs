// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-side surface for per-agent configuration — the low-frequency-write state
/// shared across every session that an agent owns. In v0.4 this is just the system
/// prompt; future pillars add tool-binding references, policy refs, persona metadata.
/// </summary>
/// <remarks>
/// Grain key is the <c>agentId</c>. One grain per agent, regardless of how many
/// sessions that agent has active. Session grains read this on activation with
/// caching; there is no live-invalidation path in v0.4 — config changes take effect
/// on a session's next deactivation + reactivation.
/// </remarks>
public interface IAgentConfigGrain : IGrainWithStringKey
{
    /// <summary>Current system prompt for this agent (may be null).</summary>
    Task<string?> GetSystemPromptAsync();

    /// <summary>Replace the system prompt. Persists immediately.</summary>
    Task SetSystemPromptAsync(string? value);

    /// <summary>Clear persisted state and deactivate on idle.</summary>
    Task DeleteAsync();
}
