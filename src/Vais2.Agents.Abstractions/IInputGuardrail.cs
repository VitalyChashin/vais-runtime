// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Runs on a fully-prepared <see cref="CompletionRequest"/> — after the history reducer,
/// system-prompt composer, context providers, and window packer — but before any
/// <see cref="IAgentFilter"/> or provider call. First <see cref="GuardrailDecision.Deny"/>
/// short-circuits the turn with an <see cref="AgentGuardrailDeniedException"/>.
/// </summary>
/// <remarks>
/// Input guardrails run on every turn in both <c>AskAsync</c> and streaming paths.
/// Typical uses: prompt-injection detection, token-budget pre-flight, PII-in-input
/// blocking, tenant policy gates.
/// </remarks>
public interface IInputGuardrail
{
    /// <summary>Evaluate the outgoing request. Return <see cref="GuardrailOutcome.Pass"/> to proceed.</summary>
    ValueTask<GuardrailOutcome> EvaluateAsync(
        CompletionRequest request,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
