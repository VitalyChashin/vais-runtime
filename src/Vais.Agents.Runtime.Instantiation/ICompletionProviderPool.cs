// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Memoises <c>ICompletionProvider</c> instances by <c>ModelSpec</c> equality
/// so guardrails or context providers that need their own model (e.g.
/// LLM-as-judge) don't re-instantiate SDK clients per turn.
/// </summary>
/// <remarks>
/// Internal to the instantiator — consumers who want provider pooling in
/// their own code paths should resolve <see cref="ICompletionProviderPool"/>
/// directly. Cache keys are <c>ModelSpec</c> records; because records compare
/// by structural equality, two specs with identical fields share a provider.
/// </remarks>
public interface ICompletionProviderPool
{
    /// <summary>
    /// Return a provider for <paramref name="spec"/>. Constructs lazily on
    /// first call; subsequent calls with an equal spec return the cached
    /// instance. Two concurrent callers for the same spec see a single
    /// construction (single-flight).
    /// </summary>
    /// <exception cref="ManifestInstantiationException">
    /// When no <see cref="IModelProviderFactory"/> matches <c>spec.Provider</c>
    /// — uses <see cref="ManifestInstantiationUrns.ModelProviderUnsupported"/>.
    /// </exception>
    ValueTask<ICompletionProvider> GetAsync(ModelSpec spec, CancellationToken cancellationToken = default);
}
