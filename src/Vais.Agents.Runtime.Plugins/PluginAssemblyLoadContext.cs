// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Runtime.Loader;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// <see cref="AssemblyLoadContext"/> for a single plugin. Resolves shared
/// types (<c>Vais.Agents.Abstractions</c>, DI / hosting / logging / options
/// abstractions, MEAI, Polly) from the runtime's default context so plugin
/// and runtime code see the same type identities. Plugin-private transitive
/// dependencies (e.g. different Newtonsoft.Json versions) load from the
/// plugin's own folder via <see cref="AssemblyDependencyResolver"/>.
/// </summary>
/// <remarks>
/// Collectible since v0.22 — enables the GC to reclaim the old load context
/// after a hot-reload swap once all plugin type references are released.
/// </remarks>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Assemblies whose type identities MUST resolve to the runtime's default
    /// context. Every DI-boundary type crossing the plugin-runtime seam lives
    /// here. Additions require a findings-doc amendment.
    /// </summary>
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.Ordinal)
    {
        // Vais.Agents ABI surface — every type crossing the plugin boundary.
        "Vais.Agents.Abstractions",
        "Vais.Agents.Core",
        "Vais.Agents.Control.Abstractions",

        // DI + hosting + logging + options + configuration abstractions.
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Configuration.Abstractions",

        // MEAI — IChatClient flows between plugin agents + runtime completion providers.
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.AI.Abstractions",

        // Polly — StatefulAgentOptions.ResiliencePipeline is a cross-boundary type.
        "Polly.Core",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string primaryAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(primaryAssemblyPath), isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryAssemblyPath);
        _resolver = new AssemblyDependencyResolver(primaryAssemblyPath);
    }

    /// <summary>Exposed for testing — the exact set of shared assemblies the plugin context defers to Default.</summary>
    internal static IReadOnlyCollection<string> SharedAssembliesForTesting => SharedAssemblies;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblies.Contains(assemblyName.Name))
        {
            // Returning null tells Default to resolve — runtime's version wins.
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
