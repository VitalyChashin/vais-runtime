// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Loads a single extension DLL from a stream into a fresh <see cref="ExtensionAssemblyLoadContext"/>,
/// validates the <see cref="VaisExtensionAttribute"/> ABI, instantiates each handler type via DI,
/// and returns a <see cref="ExtensionDescriptor"/> ready to swap into the registry.
/// </summary>
internal sealed class ExtensionAssemblyLoader
{
    /// <summary>Seam base types the loader recognises (Phase A: input + output).</summary>
    private static readonly IReadOnlyList<Type> KnownSeamTypes =
    [
        typeof(AgentInputMiddleware),
        typeof(AgentOutputMiddleware),
    ];

    /// <summary>Map from seam base type to the canonical seam name used in manifests.</summary>
    private static readonly Dictionary<Type, string> SeamNames = new()
    {
        [typeof(AgentInputMiddleware)]  = ExtensionSeams.AgentInput,
        [typeof(AgentOutputMiddleware)] = ExtensionSeams.AgentOutput,
    };

    private readonly ExtensionLoaderOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExtensionAssemblyLoader> _logger;

    public ExtensionAssemblyLoader(
        IServiceProvider serviceProvider,
        ExtensionLoaderOptions? options = null,
        ILogger<ExtensionAssemblyLoader>? logger = null)
    {
        _serviceProvider = serviceProvider;
        _options = options ?? new ExtensionLoaderOptions();
        _logger = logger ?? NullLogger<ExtensionAssemblyLoader>.Instance;
    }

    /// <summary>
    /// Load the extension DLL from <paramref name="dllStream"/> (must be seekable if null,
    /// uses <paramref name="assemblyPath"/> for dependency resolution).
    /// Returns null on failure (error already logged).
    /// </summary>
    internal ExtensionDescriptor? Load(
        ExtensionManifest manifest,
        Stream dllStream,
        string assemblyPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(dllStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyPath);

        var loadContext = new ExtensionAssemblyLoadContext(assemblyPath);
        Assembly assembly;
        try
        {
            var bytes = ReadToEnd(dllStream);
            assembly = loadContext.LoadFromStream(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{Urn}] Extension '{Id}' DLL failed to load.",
                ExtensionUrns.ExtensionLoadFailed, manifest.Id);
            return null;
        }

        var attr = assembly.GetCustomAttribute<VaisExtensionAttribute>();
        if (attr is null)
        {
            _logger.LogWarning(
                "[{Urn}] Extension '{Id}': assembly has no [VaisExtension] attribute.",
                ExtensionUrns.ExtensionLoadFailed, manifest.Id);
            return null;
        }

        if (!AbiMatches(attr.TargetApiVersion, _options.RuntimeAbiVersion))
        {
            _logger.LogWarning(
                "[{Urn}] Extension '{Id}' targets ABI '{Target}'; runtime expects '{Runtime}'.",
                ExtensionUrns.ExtensionAbiBismatch, manifest.Id, attr.TargetApiVersion, _options.RuntimeAbiVersion);
            return null;
        }

        var bindings = new List<HandlerBinding>();
        foreach (var handlerSpec in manifest.Spec.Handlers)
        {
            Type? type;
            if (string.IsNullOrEmpty(handlerSpec.TypeName))
            {
                type = AutoResolveType(attr.Handlers, handlerSpec.Seam, manifest.Id, handlerSpec.Id);
            }
            else
            {
                type = ResolveType(assembly, attr.Handlers, handlerSpec.TypeName, manifest.Id);
            }

            if (type is null)
            {
                continue;
            }

            var seam = ResolveSeam(type, manifest.Id, handlerSpec.TypeName ?? type.FullName ?? type.Name);
            if (seam is null)
            {
                continue;
            }

            object? instance;
            try
            {
                instance = ActivatorUtilities.CreateInstance(_serviceProvider, type);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Extension '{Id}' handler '{HandlerId}' (type '{Type}'): DI activation failed.",
                    manifest.Id, handlerSpec.Id, handlerSpec.TypeName);
                continue;
            }

            bindings.Add(new HandlerBinding(
                HandlerId: handlerSpec.Id,
                Seam: seam,
                Priority: handlerSpec.Priority,
                FailureMode: handlerSpec.FailureMode,
                HandlerInstance: instance));

            _logger.LogDebug(
                "Extension '{Id}' bound handler '{HandlerId}' (seam={Seam}, priority={Priority}).",
                manifest.Id, handlerSpec.Id, seam, handlerSpec.Priority);
        }

        if (bindings.Count == 0)
        {
            _logger.LogWarning(
                "[{Urn}] Extension '{Id}': no handler bindings produced — extension not registered.",
                ExtensionUrns.ExtensionLoadFailed, manifest.Id);
            return null;
        }

        _logger.LogInformation(
            "Loaded extension '{Id}' v{Version} ({Count} handler(s)).",
            manifest.Id, manifest.Version, bindings.Count);

        return new ExtensionDescriptor(
            ExtensionId: manifest.Id,
            Version: manifest.Version,
            Manifest: manifest,
            Handlers: bindings,
            LoadContext: loadContext);
    }

    /// <summary>
    /// Auto-resolve a handler type by seam name when the manifest omits <c>typeName</c>.
    /// Succeeds only when exactly one declared type matches the seam; requires explicit
    /// <c>typeName</c> when the extension exports multiple handlers for the same seam.
    /// </summary>
    private Type? AutoResolveType(Type[] declaredTypes, string seam, string extensionId, string handlerId)
    {
        var seamType = SeamNames.FirstOrDefault(kv => string.Equals(kv.Value, seam, StringComparison.OrdinalIgnoreCase)).Key;
        if (seamType is null)
        {
            _logger.LogWarning(
                "Extension '{Id}' handler '{HandlerId}': unknown seam '{Seam}' for auto-resolution.",
                extensionId, handlerId, seam);
            return null;
        }

        var candidates = Array.FindAll(declaredTypes, t => seamType.IsAssignableFrom(t));
        if (candidates.Length == 0)
        {
            _logger.LogWarning(
                "Extension '{Id}' handler '{HandlerId}': no declared type implements seam '{Seam}'.",
                extensionId, handlerId, seam);
            return null;
        }

        if (candidates.Length > 1)
        {
            _logger.LogWarning(
                "Extension '{Id}' handler '{HandlerId}': multiple types implement seam '{Seam}' — set 'typeName' to disambiguate.",
                extensionId, handlerId, seam);
            return null;
        }

        _logger.LogDebug(
            "Extension '{Id}' handler '{HandlerId}': auto-resolved to '{Type}' by seam '{Seam}'.",
            extensionId, handlerId, candidates[0].FullName, seam);
        return candidates[0];
    }

    private Type? ResolveType(Assembly assembly, Type[] declaredTypes, string typeName, string extensionId)
    {
        // First look in the types declared in [VaisExtension].
        var declared = Array.Find(declaredTypes, t =>
            string.Equals(t.FullName, typeName, StringComparison.Ordinal) ||
            string.Equals(t.Name, typeName, StringComparison.Ordinal));

        if (declared is not null)
        {
            return declared;
        }

        // Fall back to assembly scan.
        var found = assembly.GetType(typeName);
        if (found is not null)
        {
            return found;
        }

        _logger.LogWarning(
            "Extension '{Id}': handler type '{Type}' not found in assembly. Use the full CLR name.",
            extensionId, typeName);
        return null;
    }

    private string? ResolveSeam(Type handlerType, string extensionId, string typeName)
    {
        foreach (var seam in KnownSeamTypes)
        {
            if (seam.IsAssignableFrom(handlerType))
            {
                return SeamNames[seam];
            }
        }

        _logger.LogWarning(
            "Extension '{Id}': handler type '{Type}' does not extend any known seam abstract class.",
            extensionId, typeName);
        return null;
    }

    private static bool AbiMatches(string extensionVersion, string runtimeVersion)
    {
        if (string.Equals(extensionVersion, runtimeVersion, StringComparison.Ordinal))
        {
            return true;
        }

        if (TryParseMajorMinor(extensionVersion, out var ev) && TryParseMajorMinor(runtimeVersion, out var rv))
        {
            return ev == rv;
        }

        return false;
    }

    private static bool TryParseMajorMinor(string version, out (int major, int minor) result)
    {
        result = default;
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return false;
        }

        result = (major, minor);
        return true;
    }

    private static byte[] ReadToEnd(Stream stream)
    {
        if (stream is MemoryStream ms)
        {
            return ms.ToArray();
        }

        using var buf = new MemoryStream();
        stream.CopyTo(buf);
        return buf.ToArray();
    }
}
