// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// The seven universal lifecycle verbs the control plane routes through
/// <see cref="IAgentPolicyEngine"/>. Mirrors the method set on
/// <see cref="IAgentLifecycleManager"/>; extending that interface requires
/// extending this enum in lock-step.
/// </summary>
public enum PolicyOperation
{
    /// <summary>Manifest registration — creating a new agent or a new version.</summary>
    Create = 0,

    /// <summary>Run-time invocation — a caller asking the agent to handle a request.</summary>
    Invoke = 1,

    /// <summary>Out-of-band signal delivered to a running agent (e.g. cancel, approve).</summary>
    Signal = 2,

    /// <summary>Read-only query of agent state / status.</summary>
    Query = 3,

    /// <summary>Cancel an in-flight run; the manifest itself stays.</summary>
    Cancel = 4,

    /// <summary>Mutate an existing agent's manifest (label edit, config update).</summary>
    Update = 5,

    /// <summary>Remove an agent entirely — manifest + state.</summary>
    Evict = 6,

    // ── Graph operations (v0.19) ──────────────────────────────────────────

    /// <summary>Manifest registration for a graph — creating a new graph or a new version.</summary>
    GraphCreate = 7,

    /// <summary>Start a new graph run.</summary>
    GraphInvoke = 8,

    /// <summary>Resume a previously-interrupted graph run.</summary>
    GraphResume = 9,

    /// <summary>Read-only query of graph status / counters.</summary>
    GraphQuery = 10,

    /// <summary>Cancel an in-flight or interrupted graph run.</summary>
    GraphCancel = 11,

    /// <summary>Mutate an existing graph manifest.</summary>
    GraphUpdate = 12,

    /// <summary>Remove a graph manifest and all its run state.</summary>
    GraphEvict = 13,
}
