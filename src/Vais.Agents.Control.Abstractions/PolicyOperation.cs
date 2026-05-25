// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// The operations the control plane routes through <see cref="IAgentPolicyEngine"/> —
/// the seven universal agent-lifecycle verbs (mirroring <see cref="IAgentLifecycleManager"/>)
/// plus the graph, gateway-config, MCP-server, container-plugin, eval-suite, and extension
/// verbs. Adding a control-plane operation requires extending this enum in lock-step.
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

    // ── LLM gateway config operations (v0.20) ────────────────────────────

    /// <summary>Register a new LLM gateway config or version.</summary>
    LlmGatewayConfigCreate = 14,

    /// <summary>Mutate an existing LLM gateway config manifest.</summary>
    LlmGatewayConfigUpdate = 15,

    /// <summary>Read-only query of LLM gateway config status.</summary>
    LlmGatewayConfigQuery = 16,

    /// <summary>Remove an LLM gateway config manifest.</summary>
    LlmGatewayConfigEvict = 17,

    // ── MCP gateway config operations (v0.20) ────────────────────────────

    /// <summary>Register a new MCP gateway config or version.</summary>
    McpGatewayConfigCreate = 18,

    /// <summary>Mutate an existing MCP gateway config manifest.</summary>
    McpGatewayConfigUpdate = 19,

    /// <summary>Read-only query of MCP gateway config status.</summary>
    McpGatewayConfigQuery = 20,

    /// <summary>Remove an MCP gateway config manifest.</summary>
    McpGatewayConfigEvict = 21,

    // ── MCP server operations (v0.20) ─────────────────────────────────────

    /// <summary>Register a new MCP server manifest or version.</summary>
    McpServerCreate = 22,

    /// <summary>Mutate an existing MCP server manifest.</summary>
    McpServerUpdate = 23,

    /// <summary>Read-only query of MCP server status.</summary>
    McpServerQuery = 24,

    /// <summary>Remove an MCP server manifest.</summary>
    McpServerEvict = 25,

    // ── Container plugin operations (v0.24) ───────────────────────────────

    /// <summary>Register a new container plugin manifest or version.</summary>
    ContainerPluginCreate = 26,

    /// <summary>Mutate an existing container plugin manifest.</summary>
    ContainerPluginUpdate = 27,

    /// <summary>Read-only query of container plugin status.</summary>
    ContainerPluginQuery = 28,

    /// <summary>Remove a container plugin manifest and stop the container.</summary>
    ContainerPluginEvict = 29,

    // ── Eval suite operations (E1) ────────────────────────────────────────

    /// <summary>Register or overwrite an eval suite manifest (upsert).</summary>
    EvalSuiteUpsert = 30,

    /// <summary>Read-only query of eval suite manifests.</summary>
    EvalSuiteQuery = 31,

    /// <summary>Remove an eval suite manifest.</summary>
    EvalSuiteEvict = 32,

    // ── Extension operations (EXT-13) ─────────────────────────────────────

    /// <summary>Load a new extension (first registration).</summary>
    ExtensionCreate = 33,

    /// <summary>Reload an existing extension (hot-swap).</summary>
    ExtensionUpdate = 34,

    /// <summary>Read-only query of loaded extension descriptors.</summary>
    ExtensionQuery = 35,

    /// <summary>Unload an extension and remove its handler registrations.</summary>
    ExtensionEvict = 36,

    // ── Plugin (C# DLL) operations (PG-1) ────────────────────────────────────

    /// <summary>Load a new C# DLL plugin (first registration).</summary>
    PluginCreate = 37,

    /// <summary>Hot-swap an existing C# DLL plugin.</summary>
    PluginUpdate = 38,

    /// <summary>Read-only query of loaded plugin descriptors.</summary>
    PluginQuery = 39,

    /// <summary>Unload a plugin and remove its handler registrations.</summary>
    PluginEvict = 40,
}
