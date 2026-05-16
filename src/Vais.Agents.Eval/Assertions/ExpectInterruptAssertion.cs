// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when an <see cref="InterruptRaised"/> event was emitted during the case run.
/// Optional <c>interruptId</c> arg matches a specific interrupt correlation id.
/// Config: <c>{ "interruptId": "approval-required" }</c> (optional).
/// </summary>
internal sealed class ExpectInterruptAssertion : IEvalAssertion
{
    private readonly string? _interruptId;

    /// <summary>Construct with optional interrupt id filter.</summary>
    public ExpectInterruptAssertion(string? interruptId) => _interruptId = interruptId;

    /// <inheritdoc/>
    public string Kind => "expect-interrupt";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var found = run.Events
            .OfType<InterruptRaised>()
            .Any(e => _interruptId is null || string.Equals(e.InterruptId, _interruptId, StringComparison.Ordinal));

        if (found)
            return ValueTask.FromResult(new EvalAssertionResult(EvalAssertionStatus.Pass, Score: 1.0, Reason: null));

        var idNote = _interruptId is not null ? $" with interruptId='{_interruptId}'" : string.Empty;
        return ValueTask.FromResult(new EvalAssertionResult(
            EvalAssertionStatus.Fail,
            Score: 0.0,
            Reason: $"Expected an InterruptRaised event{idNote} but none was found"));
    }
}

/// <summary>Factory for <see cref="ExpectInterruptAssertion"/>.</summary>
internal sealed class ExpectInterruptAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "expect-interrupt";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        string? interruptId = null;
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("interruptId", out var el)
            && el.ValueKind == JsonValueKind.String)
        {
            interruptId = el.GetString();
        }

        return new ExpectInterruptAssertion(interruptId);
    }
}
