// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Code-mode binding for an agent. When <see cref="Enabled"/> is <c>true</c>, the agent is
/// presented a single <c>run_code</c> tool plus a generated JS API over its authorised tools,
/// and executes LLM-authored scripts in a sandboxed runtime (a hardened Jint sidecar) instead
/// of issuing per-tool JSON tool calls. The script's tool calls route back through the MCP
/// gateway exactly as normal tool calls do, so governance, budgets, and tracing are unchanged.
/// </summary>
/// <remarks>
/// Everything is optional; an agent without a <see cref="CodeModeSpec"/> uses classic
/// tool-calling. v1 supports <see cref="Runtime"/> = <c>"jint"</c> and <see cref="Generator"/>
/// = <c>"raw"</c> (a flat MCP-derived API); the <c>"ontology"</c> generator is a planned
/// extension that emits a typed concept API from an MCP server's ontology binding.
/// </remarks>
public sealed record CodeModeSpec
{
    /// <summary>Opt-in flag. When <c>false</c> (the default), the spec is ignored and the agent uses classic tool-calling.</summary>
    public bool Enabled { get; init; }

    /// <summary>Sandbox runtime backend. v1 supports <c>"jint"</c> (managed JavaScript) only.</summary>
    public string Runtime { get; init; } = "jint";

    /// <summary>Tool-API surface generator. v1 supports <c>"raw"</c> (flat MCP-derived API); <c>"ontology"</c> is deferred.</summary>
    public string Generator { get; init; } = "raw";

    /// <summary>Tool names exposed to scripts. Null inherits the agent's full authorised tool set.</summary>
    public IReadOnlyList<string>? Toolset { get; init; }

    /// <summary>Resource caps applied to each script execution. Null uses the <see cref="CodeModeLimits"/> defaults.</summary>
    public CodeModeLimits? Limits { get; init; }
}

/// <summary>
/// Resource caps for a single code-mode script execution. Enforced cooperatively by the Jint
/// engine (timeout / statement / memory / recursion) and by the runtime bridge (tool-call and
/// output-size caps). Defaults target small gateway-only orchestration scripts.
/// </summary>
public sealed record CodeModeLimits
{
    /// <summary>Wall-clock timeout for the script, in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 5000;

    /// <summary>Maximum interpreted statements before the engine aborts (infinite-loop guard).</summary>
    public int MaxStatements { get; init; } = 100_000;

    /// <summary>Maximum heap the script may allocate, in bytes.</summary>
    public long MemoryBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Maximum size of the serialized script result returned to the model, in bytes.</summary>
    public int MaxOutputBytes { get; init; } = 16_384;

    /// <summary>Maximum number of tool calls a single script may issue through the gateway bridge.</summary>
    public int MaxToolCalls { get; init; } = 32;

    /// <summary>Maximum call-stack depth (deep-recursion guard).</summary>
    public int RecursionDepth { get; init; } = 64;
}
