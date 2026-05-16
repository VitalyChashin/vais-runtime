// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Evaluates a JSONPath expression against <see cref="EvalRunRecord.FinalState"/>.
/// Returns <see cref="EvalAssertionStatus.Skipped"/> when FinalState is null (agent targets).
/// Config: <c>{ "path": "$.research_plan", "op": "equals" | "contains" | "regex", "value": "..." }</c>.
/// </summary>
/// <remarks>
/// Supports simple dot-notation paths: <c>$.field</c> and <c>$.field.subfield</c>.
/// Full JSONPath expressions are not supported.
/// </remarks>
internal sealed class GraphFinalStateAssertion : IEvalAssertion
{
    private readonly string _path;
    private readonly string _op;
    private readonly string _value;

    /// <summary>Construct with path, operator, and expected value.</summary>
    public GraphFinalStateAssertion(string path, string op, string value)
    {
        _path = path;
        _op = op;
        _value = value;
    }

    /// <inheritdoc/>
    public string Kind => "graph-final-state";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        if (run.FinalState is null)
            return ValueTask.FromResult(new EvalAssertionResult(
                EvalAssertionStatus.Skipped,
                Score: null,
                Reason: "FinalState is null — this assertion only applies to graph targets"));

        var node = ResolvePath(run.FinalState, _path);
        if (node is null)
            return ValueTask.FromResult(new EvalAssertionResult(
                EvalAssertionStatus.Fail,
                Score: 0.0,
                Reason: $"Path '{_path}' not found in FinalState"));

        var actual = node.Value.ValueKind == JsonValueKind.String
            ? node.Value.GetString() ?? string.Empty
            : node.Value.GetRawText();

        var pass = _op switch
        {
            "equals"   => string.Equals(actual, _value, StringComparison.Ordinal),
            "contains" => actual.Contains(_value, StringComparison.Ordinal),
            "regex"    => Regex.IsMatch(actual, _value),
            _          => throw new InvalidOperationException($"Unknown op '{_op}'. Supported: equals, contains, regex"),
        };

        return ValueTask.FromResult(new EvalAssertionResult(
            pass ? EvalAssertionStatus.Pass : EvalAssertionStatus.Fail,
            Score: pass ? 1.0 : 0.0,
            Reason: pass ? null : $"graph-final-state[{_path}] {_op} '{_value}' failed; actual='{actual}'"));
    }

    // Minimal JSONPath: supports "$.field" and "$.field.sub.leaf" (no wildcards/predicates).
    private static JsonElement? ResolvePath(IReadOnlyDictionary<string, JsonElement> state, string path)
    {
        // Strip leading "$." prefix
        var normalized = path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : path.TrimStart('$');
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return null;

        if (!state.TryGetValue(parts[0], out var current)) return null;

        for (var i = 1; i < parts.Length; i++)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(parts[i], out current))
                return null;
        }

        return current;
    }
}

/// <summary>Factory for <see cref="GraphFinalStateAssertion"/>.</summary>
internal sealed class GraphFinalStateAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "graph-final-state";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        var path  = GetString(args, "path",  "$.result");
        var op    = GetString(args, "op",    "equals");
        var value = GetString(args, "value", string.Empty);
        return new GraphFinalStateAssertion(path, op, value);
    }

    private static string GetString(JsonElement el, string key, string fallback)
        => el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
}
