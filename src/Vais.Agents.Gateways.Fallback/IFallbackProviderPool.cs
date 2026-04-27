// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.Fallback;

/// <summary>
/// Provides a pool of <see cref="ICompletionProvider"/> instances for fallback and load-balancing middleware.
/// </summary>
public interface IFallbackProviderPool
{
    /// <summary>
    /// Returns the ordered list of providers in this pool.
    /// </summary>
    IReadOnlyList<ICompletionProvider> GetProviders();
}
