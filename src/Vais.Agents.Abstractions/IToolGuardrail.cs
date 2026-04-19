// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Runs around an individual tool invocation. Both hooks may <see cref="GuardrailDecision.Deny"/>
/// and short-circuit the turn with an <see cref="AgentGuardrailDeniedException"/>.
/// </summary>
/// <remarks>
/// <b>Not wired in v0.4.</b> The host-side hook for tool invocations lands with the
/// execution-loop pillar (§9.5) when <c>IToolCallDispatcher</c> introduces a per-call
/// seam that lives on the library side (today the adapter SDKs — SK, MAF — own the
/// loop). The interface ships now so downstream packages can start writing guardrail
/// implementations against a stable contract.
/// </remarks>
public interface IToolGuardrail
{
    /// <summary>Evaluate a tool invocation about to fire. Denial prevents the call.</summary>
    ValueTask<GuardrailOutcome> BeforeInvokeAsync(
        ITool tool,
        JsonElement arguments,
        AgentContext context,
        CancellationToken cancellationToken = default);

    /// <summary>Evaluate the result of a tool invocation. Denial aborts the turn after the call completed.</summary>
    ValueTask<GuardrailOutcome> AfterInvokeAsync(
        ITool tool,
        JsonElement arguments,
        string result,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
