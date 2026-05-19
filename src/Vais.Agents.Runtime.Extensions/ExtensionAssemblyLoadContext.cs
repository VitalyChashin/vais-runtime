// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using System.Runtime.Loader;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// <see cref="AssemblyLoadContext"/> for a single extension. Resolves shared types
/// (Vais.Agents.Abstractions, DI / hosting / logging / options abstractions, MEAI, Polly)
/// from the runtime's default context so extension and runtime code see the same type identities.
/// Extension-private transitive dependencies load from the extension's own DLL stream via
/// <see cref="AssemblyDependencyResolver"/>.
/// </summary>
/// <remarks>
/// Collectible — enables the GC to reclaim the old load context after a hot-reload swap
/// once all extension type references are released. Per OQ-2b, this is an independent
/// copy of PluginAssemblyLoadContext; do not factor a shared base class.
/// </remarks>
public sealed class ExtensionAssemblyLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Assemblies whose type identities MUST resolve to the runtime's default context.
    /// Every type crossing the extension–runtime seam lives here.
    /// </summary>
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.Ordinal)
    {
        // Vais.Agents ABI surface — every type crossing the extension boundary.
        "Vais.Agents.Abstractions",
        "Vais.Agents.Core",
        "Vais.Agents.Control.Abstractions",

        // DI + hosting + logging + options + configuration abstractions.
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Hosting.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Configuration.Abstractions",

        // MEAI — IChatClient may cross the extension boundary.
        "Microsoft.Extensions.AI",
        "Microsoft.Extensions.AI.Abstractions",

        // Polly — ResiliencePipeline may be passed across the boundary.
        "Polly.Core",
    };

    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>Creates a collectible ALC for the extension at <paramref name="primaryAssemblyPath"/>.</summary>
    public ExtensionAssemblyLoadContext(string primaryAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(primaryAssemblyPath), isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryAssemblyPath);
        _resolver = new AssemblyDependencyResolver(primaryAssemblyPath);
    }

    internal static IReadOnlyCollection<string> SharedAssembliesForTesting => SharedAssemblies;

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblies.Contains(assemblyName.Name))
        {
            return null; // Defer to Default context — runtime's version wins.
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    /// <inheritdoc/>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
