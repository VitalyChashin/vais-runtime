// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// A single code-mode script execution request sent from the runtime to the ScriptRuntime
/// sidecar over <c>POST /v1/script/run</c>. The sidecar concatenates <see cref="Prelude"/>
/// (the runtime-generated <c>tools.*</c> JS API) with the LLM-authored <see cref="Script"/>,
/// runs it under <see cref="Limits"/>, and routes the script's tool calls back to
/// <see cref="ToolGatewayUrl"/> authenticated with <see cref="CallToken"/>.
/// </summary>
public sealed record ScriptRunRequest
{
    /// <summary>Run identifier — flows to the gateway as <c>X-Run-Id</c> and groups telemetry.</summary>
    public required string RunId { get; init; }

    /// <summary>Agent identifier — flows to the gateway as <c>X-Agent-Id</c>; the gateway resolves this agent's authorised tools.</summary>
    public required string AgentId { get; init; }

    /// <summary>Runtime-generated JS prelude defining the <c>tools</c> object; each method compiles to a single <c>__callTool</c> host call.</summary>
    public required string Prelude { get; init; }

    /// <summary>The LLM-authored script body to execute after the prelude.</summary>
    public required string Script { get; init; }

    /// <summary>Absolute URL of the runtime's container-gateway tool-invoke endpoint the script's <c>__callTool</c> bridge posts to.</summary>
    public required string ToolGatewayUrl { get; init; }

    /// <summary>Short-turn HMAC call token authenticating the script's tool calls; never a raw provider key.</summary>
    public required string CallToken { get; init; }

    /// <summary>Resource caps applied to the execution. Defaults are the <see cref="CodeModeLimits"/> defaults.</summary>
    public CodeModeLimits Limits { get; init; } = new();

    /// <summary>Optional W3C <c>traceparent</c> so the sidecar's per-script span anchors under the run/node trace.</summary>
    public string? Traceparent { get; init; }
}
