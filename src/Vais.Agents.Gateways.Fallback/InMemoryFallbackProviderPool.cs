// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.Fallback;

/// <summary>
/// In-memory <see cref="IFallbackProviderPool"/> backed by a fixed list of providers.
/// </summary>
public sealed class InMemoryFallbackProviderPool : IFallbackProviderPool
{
    private readonly IReadOnlyList<ICompletionProvider> _providers;

    /// <summary>
    /// Initializes a new pool with the given providers (tried in order by fallback middleware).
    /// </summary>
    public InMemoryFallbackProviderPool(params ICompletionProvider[] providers)
        => _providers = providers;

    /// <inheritdoc/>
    public IReadOnlyList<ICompletionProvider> GetProviders() => _providers;
}
