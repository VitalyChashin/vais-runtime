// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Runtime.Plugins;

internal sealed class PluginHandlerRegistry : IPluginHandlerRegistry
{
    private readonly ConcurrentDictionary<string, IAgentHandlerFactory> _factories = new(StringComparer.Ordinal);
    private readonly List<PluginDescriptor> _plugins = new();
    private readonly Lock _pluginsLock = new();
    private readonly SemaphoreSlim _swapLock = new(1, 1);

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

    /// <summary>
    /// Atomically replaces the handler entries and plugin descriptor for
    /// <paramref name="pluginName"/> with those from the newly loaded plugin.
    /// Returns the old <see cref="PluginDescriptor"/> so the caller can
    /// schedule grain deactivation against the previous load context.
    /// </summary>
    internal async Task<PluginDescriptor?> SwapAsync(
        string pluginName,
        PluginDescriptor newDescriptor,
        IReadOnlyDictionary<string, IAgentHandlerFactory> newFactories,
        CancellationToken cancellationToken = default)
    {
        await _swapLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PluginDescriptor? oldDescriptor;
            lock (_pluginsLock)
            {
                oldDescriptor = _plugins.FirstOrDefault(p => p.Name == pluginName);
                if (oldDescriptor is not null)
                {
                    _plugins.Remove(oldDescriptor);
                }
                _plugins.Add(newDescriptor);
            }

            if (oldDescriptor is not null)
            {
                foreach (var handlerTypeName in oldDescriptor.Handlers)
                {
                    _factories.TryRemove(handlerTypeName, out _);
                }
            }

            foreach (var (typeName, factory) in newFactories)
            {
                _factories[typeName] = factory;
            }

            return oldDescriptor;
        }
        finally
        {
            _swapLock.Release();
        }
    }

    /// <summary>Returns a snapshot view of all currently registered factories. Used by <see cref="DefaultPluginReloader"/> to extract factories from a temporary registry.</summary>
    internal IReadOnlyDictionary<string, IAgentHandlerFactory> GetAllFactories() => _factories;
}
