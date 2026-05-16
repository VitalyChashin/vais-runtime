// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Core.Guardrails;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when an LLM-as-judge scores the agent response at or above
/// <c>minScore</c>.
/// Config: <c>{ "prompt": "...", "minScore": 0.7, "judgeModel": "alias" }</c>.
/// <c>judgeModel</c> may be omitted when the suite-level
/// <see cref="EvalDefaults.JudgeModel"/> is set.
/// </summary>
internal sealed class JudgeScoreAssertion : IEvalAssertion
{
    private readonly string _prompt;
    private readonly double _minScore;
    private readonly string? _judgeModelOverride;
    private readonly IModelRouter _router;

    /// <summary>DI ctor.</summary>
    public JudgeScoreAssertion(string prompt, double minScore, string? judgeModelOverride, IModelRouter router)
    {
        _prompt = prompt;
        _minScore = minScore;
        _judgeModelOverride = judgeModelOverride;
        _router = router;
    }

    /// <inheritdoc/>
    public string Kind => "judge-score";

    /// <inheritdoc/>
    public async ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var alias = _judgeModelOverride ?? ctx.Suite.Defaults?.JudgeModel;
        if (alias is null)
            return new EvalAssertionResult(EvalAssertionStatus.Error, Score: null, Reason: "No judge model configured. Set judgeModel in assertion params or suite.spec.defaults.");

        ModelRoute route;
        try
        {
            route = await _router.ResolveAsync(alias, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new EvalAssertionResult(EvalAssertionStatus.Error, Score: null, Reason: $"Model '{alias}' not found: {ex.Message}");
        }

        var score = await LlmJudgeScorer.TryScoreAsync(route.Provider, _prompt, run.ResponseText, ct).ConfigureAwait(false);
        if (score is null)
            return new EvalAssertionResult(EvalAssertionStatus.Error, Score: null, Reason: "Judge model did not return a parseable score in [0, 1]");

        var pass = score >= _minScore;
        return new EvalAssertionResult(
            pass ? EvalAssertionStatus.Pass : EvalAssertionStatus.Fail,
            Score: score,
            Reason: pass ? null : string.Format(CultureInfo.InvariantCulture, "Score {0:F2} < minScore {1:F2}", score, _minScore));
    }
}

/// <summary>Factory for <see cref="JudgeScoreAssertion"/>. Resolves <see cref="IModelRouter"/> from DI.</summary>
internal sealed class JudgeScoreAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "judge-score";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        var prompt = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("prompt", out var pEl)
            ? pEl.GetString() ?? throw new InvalidOperationException("judge-score 'prompt' must be a string")
            : throw new InvalidOperationException("judge-score requires a params object with 'prompt'");

        var minScore = 0.7;
        if (args.TryGetProperty("minScore", out var msEl) && msEl.ValueKind == JsonValueKind.Number)
            minScore = msEl.GetDouble();

        string? judgeModel = null;
        if (args.TryGetProperty("judgeModel", out var jmEl) && jmEl.ValueKind == JsonValueKind.String)
            judgeModel = jmEl.GetString();

        var router = services.GetRequiredService<IModelRouter>();
        return new JudgeScoreAssertion(prompt, minScore, judgeModel, router);
    }
}
