// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Name-keyed registry of DI-aware <c>ITool</c> factories. Manifest tool refs
/// of the form <c>"static:&lt;name&gt;"</c> resolve through this registry.
/// Absent registry ⇒ any <c>static:*</c> ref in a manifest fails translation
/// with <see cref="ManifestInstantiationUrns.ToolNotRegistered"/>.
/// </summary>
/// <remarks>
/// Registrations happen at host-startup time via
/// <c>services.AddStaticToolRegistry(b =&gt; b.Add("weather", sp =&gt; new WeatherTool(...)))</c>.
/// The factory receives <see cref="IServiceProvider"/> so tools can depend on
/// <c>IHttpClientFactory</c> / <c>ILogger&lt;T&gt;</c> / etc. without being
/// forced to cache them in closures.
/// </remarks>
public interface IStaticToolRegistry
{
    /// <summary>Resolve a named tool. Returns <c>null</c> when the name is not registered.</summary>
    ITool? Get(string name, IServiceProvider serviceProvider);
}

/// <summary>
/// Builder surface for <see cref="IStaticToolRegistry"/>. Exposed by the
/// <c>AddStaticToolRegistry</c> DI extension's delegate parameter.
/// </summary>
public interface IStaticToolRegistryBuilder
{
    /// <summary>
    /// Register a tool factory under the given name. Duplicate names throw at
    /// registration (fail fast) rather than silently overwrite.
    /// </summary>
    IStaticToolRegistryBuilder Add(string name, Func<IServiceProvider, ITool> factory);
}
