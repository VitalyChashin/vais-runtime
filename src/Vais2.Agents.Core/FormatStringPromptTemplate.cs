// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;

namespace Vais2.Agents.Core;

/// <summary>
/// Default <see cref="IPromptTemplate"/>. Replaces <c>{key}</c> occurrences with
/// <c>variables[key]?.ToString() ?? ""</c>; missing keys are left as literal
/// <c>{key}</c> text. No escaping, no conditionals, no loops — if you need those,
/// inject SK's Handlebars / Liquid engine instead.
/// </summary>
/// <remarks>
/// Braces in the template that aren't valid <c>{key}</c> tokens (e.g. unmatched
/// <c>{</c>, or a token name with whitespace) are emitted verbatim. The renderer
/// errs on the side of "don't corrupt the output"; strict validation is a
/// consumer-side concern.
/// </remarks>
public sealed class FormatStringPromptTemplate : IPromptTemplate
{
    /// <summary>Shared singleton instance. Stateless.</summary>
    public static readonly FormatStringPromptTemplate Instance = new();

    private FormatStringPromptTemplate() { }

    /// <inheritdoc />
    public ValueTask<string> RenderAsync(
        string template,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        if (template.Length == 0 || template.IndexOf('{') < 0)
        {
            return ValueTask.FromResult(template);
        }

        var sb = new StringBuilder(template.Length);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }

            sb.Append(template, i, open - i);

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                // No matching brace — emit the rest verbatim, including the stray '{'.
                sb.Append(template, open, template.Length - open);
                break;
            }

            var key = template.Substring(open + 1, close - open - 1);
            if (variables.TryGetValue(key, out var value))
            {
                sb.Append(value?.ToString() ?? string.Empty);
            }
            else
            {
                // Unknown key — emit the original token so the consumer sees
                // the intent clearly instead of a silent blank.
                sb.Append(template, open, close - open + 1);
            }

            i = close + 1;
        }

        return ValueTask.FromResult(sb.ToString());
    }
}
