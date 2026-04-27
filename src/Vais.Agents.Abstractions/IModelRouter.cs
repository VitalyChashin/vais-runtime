// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Resolves a logical model alias (e.g. "gpt-4o", "claude-3-5-sonnet")
/// to a concrete <see cref="ICompletionProvider"/> and its <see cref="ModelSpec"/>.
/// </summary>
/// <remarks>
/// Intentionally separate from <c>IFallbackProviderPool</c>: the router answers
/// "which provider handles this model alias?"; the fallback middleware answers
/// "if that provider fails, what are the fallback candidates?".
/// </remarks>
public interface IModelRouter
{
    /// <summary>
    /// Returns the provider and model spec for the given alias, or throws
    /// <see cref="ModelNotFoundException"/> if the alias is unknown.
    /// </summary>
    ValueTask<ModelRoute> ResolveAsync(
        string modelAlias,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all model aliases known to this router.
    /// Used to populate <c>GET /v1/models</c> on the OpenAI-compatible transport.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListAliasesAsync(
        CancellationToken cancellationToken = default);
}
