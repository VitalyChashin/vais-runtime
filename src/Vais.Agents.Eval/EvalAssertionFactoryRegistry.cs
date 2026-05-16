// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;

namespace Vais.Agents.Eval;

/// <summary>
/// Default <see cref="IEvalAssertionFactoryRegistry"/> backed by DI-registered
/// <see cref="IEvalAssertionFactory"/> instances. Keyed case-insensitively.
/// </summary>
public sealed class EvalAssertionFactoryRegistry : IEvalAssertionFactoryRegistry
{
    private readonly Dictionary<string, IEvalAssertionFactory> _factories;

    /// <summary>DI ctor — collects all registered factories.</summary>
    public EvalAssertionFactoryRegistry(IEnumerable<IEvalAssertionFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.Kind, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> RegisteredKinds => _factories.Keys.ToArray();

    /// <inheritdoc/>
    public bool TryGet(string kind, [NotNullWhen(true)] out IEvalAssertionFactory? factory)
        => _factories.TryGetValue(kind, out factory);
}
