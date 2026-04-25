// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Runtime registry mapping <c>AgentHandlerRef.TypeName</c> → loaded
/// <see cref="IAgentHandlerFactory"/>. Populated by
/// <see cref="AssemblyPluginLoader"/> at silo startup; queried by the v0.18
/// manifest translator to decide whether a manifest's <c>Handler.TypeName</c>
/// routes to a plugin or falls through to the v0.17 declarative path.
/// Also supports runtime registration of Python-backed agent handlers (v0.24).
/// </summary>
public interface IPluginHandlerRegistry
{
    /// <summary>
    /// Register a handler factory. Throws <see cref="PluginLoadException"/> with
    /// <see cref="PluginUrns.PluginHandlerCollision"/> if <paramref name="factory"/>'s
    /// <c>HandlerTypeName</c> is already registered.
    /// </summary>
    void Register(IAgentHandlerFactory factory, string ownerPluginName);

    /// <summary>Look up a factory by <c>Handler.TypeName</c>. Returns <c>true</c> when found.</summary>
    bool TryGet(string handlerTypeName, out IAgentHandlerFactory? factory);

    /// <summary>Enumerate every registered handler type name (for diagnostics / startup logs / the v0.18.x <c>/v1/plugins</c> endpoint).</summary>
    IReadOnlyCollection<string> HandlerTypeNames { get; }

    /// <summary>Enumerate every loaded plugin descriptor (for diagnostics / startup logs).</summary>
    IReadOnlyCollection<PluginDescriptor> Plugins { get; }
}
