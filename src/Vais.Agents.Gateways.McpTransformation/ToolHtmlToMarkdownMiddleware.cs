// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text.RegularExpressions;

namespace Vais.Agents.Gateways.McpTransformation;

/// <summary>
/// Gateway middleware that converts HTML tool responses to plain text before they
/// re-enter the agent's context window. Only applies when the response looks like HTML.
/// </summary>
/// <remarks>
/// Strips HTML tags using a regex and decodes HTML entities. Non-HTML responses are returned unchanged.
/// Error outcomes are never transformed.
/// </remarks>
public sealed partial class ToolHtmlToMarkdownMiddleware : ToolGatewayMiddleware
{
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagPattern();

    /// <inheritdoc/>
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken)
    {
        var outcome = await next().ConfigureAwait(false);
        if (outcome.Error is not null || outcome.Result is null) return outcome;
        if (!LooksLikeHtml(outcome.Result)) return outcome;
        var stripped = HtmlTagPattern().Replace(outcome.Result, string.Empty);
        var decoded = WebUtility.HtmlDecode(stripped);
        return outcome with { Result = decoded };
    }

    private static bool LooksLikeHtml(string s)
        => s.TrimStart().StartsWith('<') && s.Contains('<') && s.Contains('>');
}
