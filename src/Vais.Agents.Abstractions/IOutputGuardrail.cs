// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Runs on the <see cref="CompletionResponse"/> after the provider returns, before the
/// assistant turn is appended to the session. First <see cref="GuardrailDecision.Deny"/>
/// short-circuits the turn with an <see cref="AgentGuardrailDeniedException"/>; the
/// assistant turn is NOT appended to the session in that case.
/// </summary>
/// <remarks>
/// <para>
/// In streaming turns (<c>StatefulAiAgent.StreamAsync</c>), output guardrails run
/// <em>after</em> the full response has been accumulated — deltas already went to
/// the consumer by then. Consumers who need strict pre-emit gating on streaming
/// responses should stay on <c>AskAsync</c> or ship a custom streaming filter.
/// </para>
/// </remarks>
public interface IOutputGuardrail
{
    /// <summary>Evaluate the provider's response. Return <see cref="GuardrailOutcome.Pass"/> to append and continue.</summary>
    ValueTask<GuardrailOutcome> EvaluateAsync(
        CompletionResponse response,
        AgentContext context,
        CancellationToken cancellationToken = default);
}
