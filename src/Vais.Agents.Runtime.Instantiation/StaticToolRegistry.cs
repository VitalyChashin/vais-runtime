// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Runtime.Instantiation;

internal sealed class StaticToolRegistry : IStaticToolRegistry
{
    private readonly IReadOnlyDictionary<string, Func<IServiceProvider, ITool>> _factories;

    public StaticToolRegistry(IReadOnlyDictionary<string, Func<IServiceProvider, ITool>> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = factories;
    }

    public ITool? Get(string name, IServiceProvider serviceProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return _factories.TryGetValue(name, out var factory) ? factory(serviceProvider) : null;
    }
}

internal sealed class StaticToolRegistryBuilder : IStaticToolRegistryBuilder
{
    private readonly ConcurrentDictionary<string, Func<IServiceProvider, ITool>> _factories = new(StringComparer.Ordinal);

    public IStaticToolRegistryBuilder Add(string name, Func<IServiceProvider, ITool> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);

        if (!_factories.TryAdd(name, factory))
        {
            throw new InvalidOperationException(
                $"A static tool named '{name}' is already registered. Tool names must be unique within an IStaticToolRegistry.");
        }

        return this;
    }

    public IStaticToolRegistry Build() => new StaticToolRegistry(_factories);
}
