// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when no <see cref="GuardrailTriggered"/> event with
/// <see cref="GuardrailDecision.Deny"/> was emitted during the case run.
/// Optional <c>layer</c> arg narrows the check to a specific guardrail layer.
/// Config: <c>{ "layer": "Output" }</c> (optional).
/// </summary>
internal sealed class NoGuardrailDenyAssertion : IEvalAssertion
{
    private readonly GuardrailLayer? _layer;

    /// <summary>Construct with optional layer filter.</summary>
    public NoGuardrailDenyAssertion(GuardrailLayer? layer) => _layer = layer;

    /// <inheritdoc/>
    public string Kind => "no-guardrail-deny";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var denial = run.Events
            .OfType<GuardrailTriggered>()
            .FirstOrDefault(g => g.Decision == GuardrailDecision.Deny
                                  && (_layer is null || g.Layer == _layer));

        if (denial is null)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        var layerNote = _layer.HasValue ? $" [{_layer.Value}]" : string.Empty;
        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"Guardrail deny{layerNote}: {denial.Reason ?? "(no reason)"}"));
    }
}

/// <summary>Factory for <see cref="NoGuardrailDenyAssertion"/>.</summary>
internal sealed class NoGuardrailDenyAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "no-guardrail-deny";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        GuardrailLayer? layer = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("layer", out var lEl)
            && lEl.ValueKind == JsonValueKind.String)
        {
            var layerStr = lEl.GetString();
            if (Enum.TryParse<GuardrailLayer>(layerStr, ignoreCase: true, out var parsed))
                layer = parsed;
        }

        return new NoGuardrailDenyAssertion(layer);
    }
}
