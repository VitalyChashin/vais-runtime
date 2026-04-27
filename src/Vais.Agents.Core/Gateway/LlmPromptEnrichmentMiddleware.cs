// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Gateway middleware that appends or prepends a fixed string to every request's system prompt.
/// Useful for injecting platform-level instructions (workspace context, safety notices) without
/// touching individual agent manifests. Runs on both non-streaming and streaming paths.
/// </summary>
/// <remarks>
/// Construct directly with the desired prefix and/or suffix; no DI registration needed.
/// <code>
/// new StatefulAgentOptions
/// {
///     GatewayMiddleware = [new LlmPromptEnrichmentMiddleware(
///         suffix: "\n\nAlways respond in the user's language.")]
/// }
/// </code>
/// </remarks>
public sealed class LlmPromptEnrichmentMiddleware(
    string prefix = "",
    string suffix = "") : LlmGatewayMiddleware
{
    /// <inheritdoc/>
    protected override Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
        => next(Enrich(request), cancellationToken);

    /// <inheritdoc/>
    protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => next(Enrich(request), cancellationToken);

    private CompletionRequest Enrich(CompletionRequest request)
    {
        if (prefix.Length == 0 && suffix.Length == 0) return request;
        var original = request.SystemPrompt ?? string.Empty;
        return request with { SystemPrompt = $"{prefix}{original}{suffix}" };
    }
}
