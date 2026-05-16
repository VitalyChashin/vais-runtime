// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vais.Agents.Eval.Assertions;

/// <summary>
/// Passes when the agent response text matches a regular expression.
/// Config: <c>{ "pattern": "hello", "ignoreCase": true }</c>.
/// </summary>
internal sealed class ResponseRegexAssertion : IEvalAssertion
{
    private readonly Regex _regex;

    /// <summary>Construct with a compiled pattern.</summary>
    public ResponseRegexAssertion(string pattern, bool ignoreCase = false)
    {
        var opts = RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        _regex = new Regex(pattern, opts);
    }

    /// <inheritdoc/>
    public string Kind => "response-regex";

    /// <inheritdoc/>
    public ValueTask<EvalAssertionResult> EvaluateAsync(EvalCaseContext ctx, EvalRunRecord run, CancellationToken ct)
    {
        var matched = _regex.IsMatch(run.ResponseText);
        return ValueTask.FromResult(new EvalAssertionResult(
            matched ? EvalAssertionStatus.Pass : EvalAssertionStatus.Fail,
            Score: matched ? 1.0 : 0.0,
            Reason: matched ? null : $"Response did not match pattern /{_regex}/"));
    }
}

/// <summary>Factory for <see cref="ResponseRegexAssertion"/>. Reads <c>pattern</c> and optional <c>ignoreCase</c>.</summary>
internal sealed class ResponseRegexAssertionFactory : IEvalAssertionFactory
{
    /// <inheritdoc/>
    public string Kind => "response-regex";

    /// <inheritdoc/>
    public IEvalAssertion Create(JsonElement args, IServiceProvider services)
    {
        var pattern = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("pattern", out var p)
            ? p.GetString() ?? throw new InvalidOperationException("response-regex requires 'pattern'")
            : throw new InvalidOperationException("response-regex requires a params object with 'pattern'");

        var ignoreCase = args.TryGetProperty("ignoreCase", out var ic) && ic.ValueKind == JsonValueKind.True;

        return new ResponseRegexAssertion(pattern, ignoreCase);
    }
}
