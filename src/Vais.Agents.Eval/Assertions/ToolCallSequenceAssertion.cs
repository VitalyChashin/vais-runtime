// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Evaluates tool-call sequences from the agent journal.
/// Config: <c>{ "expected": ["tool-a", "tool-b"], "scoring": "f1" | "exact" | "subsequence" }</c>.
/// Default scoring: <c>f1</c> (precision/recall F1 over tool names, set semantics).
/// </summary>
internal sealed class ToolCallSequenceAssertion : IEvalAssertion
{
    private readonly string[] _expected;
    private readonly string _scoring;

    /// <summary>Construct with expected tool names and scoring strategy.</summary>
    public ToolCallSequenceAssertion(string[] expected, string scoring = "f1")
    {
        _expected = expected;
        _scoring = scoring;
    }

    /// <inheritdoc/>
    public string Kind => "tool-call-sequence";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var actual = run.JournalEntries.OfType<ToolCallRecorded>().Select(e => e.ToolName).ToArray();

        if (_scoring == "exact")
        {
            var match = actual.SequenceEqual(_expected, StringComparer.Ordinal);
            return ValueTask.FromResult(new EvalAssertionResult(
                match ? EvalAssertionStatus.Pass : EvalAssertionStatus.Fail,
                Score: match ? 1.0 : 0.0,
                Reason: match ? null : $"Expected exact sequence [{string.Join(", ", _expected)}] but got [{string.Join(", ", actual)}]"));
        }

        if (_scoring == "subsequence")
        {
            var isSubseq = IsSubsequence(_expected, actual);
            return ValueTask.FromResult(new EvalAssertionResult(
                isSubseq ? EvalAssertionStatus.Pass : EvalAssertionStatus.Fail,
                Score: isSubseq ? 1.0 : 0.0,
                Reason: isSubseq ? null : $"Expected subsequence [{string.Join(", ", _expected)}] not found in [{string.Join(", ", actual)}]"));
        }

        // F1 scoring (default) — set-based
        var tp = _expected.Count(e => actual.Contains(e, StringComparer.Ordinal));
        var precision = actual.Length == 0 ? (_expected.Length == 0 ? 1.0 : 0.0) : (double)tp / actual.Length;
        var recall = _expected.Length == 0 ? 1.0 : (double)tp / _expected.Length;
        var f1 = precision + recall == 0 ? 0.0 : 2 * precision * recall / (precision + recall);
        var pass = f1 >= 1.0 - 1e-9;

        return ValueTask.FromResult(new EvalAssertionResult(
            pass ? EvalAssertionStatus.Pass : EvalAssertionStatus.Fail,
            Score: f1,
            Reason: pass ? null : $"Tool-call F1={f1:F2} (expected=[{string.Join(", ", _expected)}], actual=[{string.Join(", ", actual)}])"));
    }

    private static bool IsSubsequence(string[] needle, string[] haystack)
    {
        var j = 0;
        foreach (var h in haystack)
        {
            if (j < needle.Length && string.Equals(needle[j], h, StringComparison.Ordinal))
                j++;
        }
        return j == needle.Length;
    }
}

/// <summary>Factory for <see cref="ToolCallSequenceAssertion"/>.</summary>
internal sealed class ToolCallSequenceAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "tool-call-sequence";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        string[] expected = Array.Empty<string>();
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("expected", out var expEl)
            && expEl.ValueKind == JsonValueKind.Array)
        {
            expected = expEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        }

        var scoring = "f1";
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("scoring", out var scEl)
            && scEl.ValueKind == JsonValueKind.String)
        {
            scoring = scEl.GetString() ?? "f1";
        }

        return new ToolCallSequenceAssertion(expected, scoring);
    }
}
