// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Runtime.Plugins;

internal sealed class PluginHandlerRegistry : IPluginHandlerRegistry
{
    private readonly ConcurrentDictionary<string, IAgentHandlerFactory> _factories = new(StringComparer.Ordinal);
    private readonly List<PluginDescriptor> _plugins = new();
    private readonly Lock _pluginsLock = new();

    /// <summary>Register a factory. Throws <see cref="PluginLoadException"/> with <see cref="PluginUrns.PluginHandlerCollision"/> on duplicate TypeName.</summary>
    public void Register(IAgentHandlerFactory factory, string ownerPluginName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPluginName);

        if (!_factories.TryAdd(factory.HandlerTypeName, factory))
        {
            throw new PluginLoadException(
                PluginUrns.PluginHandlerCollision,
                $"Two plugins export handler '{factory.HandlerTypeName}'. Plugin '{ownerPluginName}' cannot register; another plugin got there first. Rename or remove one of them.",
                ownerPluginName);
        }
    }

    /// <summary>Record a loaded plugin descriptor for diagnostics.</summary>
    public void RecordPlugin(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_pluginsLock)
        {
            _plugins.Add(descriptor);
        }
    }

    /// <inheritdoc />
    public bool TryGet(string handlerTypeName, out IAgentHandlerFactory? factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerTypeName);
        return _factories.TryGetValue(handlerTypeName, out factory);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> HandlerTypeNames => _factories.Keys.ToArray();

    /// <inheritdoc />
    public IReadOnlyCollection<PluginDescriptor> Plugins
    {
        get
        {
            lock (_pluginsLock)
            {
                return _plugins.ToArray();
            }
        }
    }
}
