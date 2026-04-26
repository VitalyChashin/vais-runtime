// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Scans a plugin directory (one subfolder per plugin) + loads each plugin
/// into its own <see cref="PluginAssemblyLoadContext"/>. Registers discovered
/// <see cref="IAgentHandlerFactory"/> implementations in the supplied
/// registry; auto-wraps <see cref="IAiAgent"/>-direct implementations via
/// <see cref="DefaultHandlerFactory{TAgent}"/> when
/// <see cref="PluginLoaderOptions.AllowConventionDiscovery"/> is enabled.
/// </summary>
public sealed class AssemblyPluginLoader
{
    private readonly PluginLoaderOptions _options;
    private readonly ILogger<AssemblyPluginLoader> _logger;

    /// <summary>Construct a loader with the given options + optional logger.</summary>
    public AssemblyPluginLoader(PluginLoaderOptions? options = null, ILogger<AssemblyPluginLoader>? logger = null)
    {
        _options = options ?? new PluginLoaderOptions();
        _logger = logger ?? NullLogger<AssemblyPluginLoader>.Instance;
    }

    /// <summary>
    /// Scan <paramref name="pluginsDirectory"/>, load each subfolder as a
    /// plugin, populate <paramref name="registry"/>. Missing / empty / unreadable
    /// directory → no-op with a WARN log; per-plugin failures log WARN and
    /// continue; handler-name collisions throw unless
    /// <see cref="PluginLoaderOptions.FailOnHandlerCollision"/> is false.
    /// </summary>
    internal void Load(string pluginsDirectory, PluginHandlerRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        ArgumentNullException.ThrowIfNull(registry);

        if (!Directory.Exists(pluginsDirectory))
        {
            _logger.LogInformation(
                "Plugins directory '{Dir}' does not exist — plugin loading skipped.",
                pluginsDirectory);
            return;
        }

        var subfolders = Directory.GetDirectories(pluginsDirectory);
        if (subfolders.Length == 0)
        {
            _logger.LogInformation(
                "Plugins directory '{Dir}' is empty — no plugins to load.",
                pluginsDirectory);
            return;
        }

        foreach (var folder in subfolders)
        {
            TryLoadPlugin(folder, registry);
        }

        _logger.LogInformation(
            "Plugin loading complete — {PluginCount} plugin(s) loaded, {HandlerCount} handler(s) registered.",
            registry.Plugins.Count,
            registry.HandlerTypeNames.Count);
    }

    /// <summary>
    /// Load exactly one plugin from <paramref name="pluginFolder"/> into
    /// <paramref name="registry"/>. Used by <see cref="DefaultPluginReloader"/>
    /// for targeted reloads without rescanning the full plugins directory.
    /// </summary>
    internal void LoadPlugin(string pluginFolder, PluginHandlerRegistry registry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginFolder);
        ArgumentNullException.ThrowIfNull(registry);
        TryLoadPlugin(pluginFolder, registry);
    }

    private void TryLoadPlugin(string folder, PluginHandlerRegistry registry)
    {
        var pluginName = Path.GetFileName(folder);
        var primaryAssembly = ResolvePrimaryAssembly(folder, pluginName);

        if (primaryAssembly is null)
        {
            _logger.LogWarning(
                "Plugin '{Plugin}' has no loadable primary assembly in {Folder} — skipped.",
                pluginName,
                folder);
            return;
        }

        PluginAssemblyLoadContext loadContext;
        Assembly assembly;
        try
        {
            loadContext = new PluginAssemblyLoadContext(primaryAssembly);
            assembly = loadContext.LoadFromAssemblyPath(primaryAssembly);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Plugin '{Plugin}' at {Path} failed to load: {Urn}",
                pluginName,
                primaryAssembly,
                PluginUrns.PluginLoadFailed);
            return;
        }

        var attribute = assembly.GetCustomAttribute<VaisPluginAttribute>();
        if (attribute is not null)
        {
            LoadViaAttribute(assembly, attribute, primaryAssembly, pluginName, loadContext, registry);
        }
        else if (_options.AllowConventionDiscovery)
        {
            LoadViaConvention(assembly, primaryAssembly, pluginName, loadContext, registry);
        }
        else
        {
            _logger.LogWarning(
                "Plugin '{Plugin}' has no [VaisPlugin] attribute and convention discovery is disabled — skipped.",
                pluginName);
        }
    }

    private string? ResolvePrimaryAssembly(string folder, string pluginName)
    {
        // 1. Exact match: <folder>/<pluginName>.dll
        var nameMatch = Path.Combine(folder, pluginName + ".dll");
        if (File.Exists(nameMatch)) return nameMatch;

        // Filter to runtime assemblies only; exclude satellite (.resources.dll) and native.
        var candidates = Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly)
            .Where(p => !p.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // 2. Single DLL remaining — no ambiguity.
        if (candidates.Length == 1) return candidates[0];

        // 3. Suffix match: <Something>.<pluginName>.dll — handles namespaced output names
        //    (e.g. folder "WeatherAgent", primary DLL "MyApp.WeatherAgent.dll").
        var suffixMatch = candidates.Where(p =>
            Path.GetFileNameWithoutExtension(p)
                .EndsWith("." + pluginName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (suffixMatch.Length == 1) return suffixMatch[0];

        _logger.LogWarning(
            "[{Urn}] Plugin '{Name}': cannot determine primary assembly in '{Folder}' " +
            "({Count} candidate(s): {Files}). Add a DLL named '{Name}.dll' or rename the folder to match the assembly.",
            PluginUrns.PluginLoadFailed, pluginName, folder, candidates.Length,
            string.Join(", ", candidates.Select(Path.GetFileName)), pluginName);
        return null;
    }

    private void LoadViaAttribute(
        Assembly assembly,
        VaisPluginAttribute attribute,
        string assemblyPath,
        string pluginName,
        PluginAssemblyLoadContext loadContext,
        PluginHandlerRegistry registry)
    {
        if (!AbiMatches(attribute.TargetApiVersion, _options.RuntimeAbiVersion))
        {
            _logger.LogWarning(
                "[{Urn}] Plugin '{Plugin}' targets ABI '{Target}'; runtime expects '{Runtime}'. " +
                "Update [assembly: VaisPlugin(targetApiVersion: \"{Runtime}\")] and rebuild the plugin to load it.",
                PluginUrns.PluginAbiMismatch,
                pluginName,
                attribute.TargetApiVersion,
                _options.RuntimeAbiVersion,
                _options.RuntimeAbiVersion);
            return;
        }

        var registered = 0;
        foreach (var handlerTypeName in attribute.Handlers)
        {
            if (TryRegisterHandler(assembly, handlerTypeName, pluginName, registry))
            {
                registered++;
            }
        }

        if (registered > 0)
        {
            registry.RecordPlugin(new PluginDescriptor(
                Name: pluginName,
                AssemblyPath: assemblyPath,
                TargetApiVersion: attribute.TargetApiVersion,
                Handlers: attribute.Handlers,
                LoadedViaAttribute: true,
                LoadContext: loadContext));

            _logger.LogInformation(
                "Loaded plugin '{Plugin}' (targetApiVersion={Abi}, handlers=[{Handlers}])",
                pluginName,
                attribute.TargetApiVersion,
                string.Join(", ", attribute.Handlers));
        }
    }

    private void LoadViaConvention(
        Assembly assembly,
        string assemblyPath,
        string pluginName,
        PluginAssemblyLoadContext loadContext,
        PluginHandlerRegistry registry)
    {
        var handlers = new List<string>();
        foreach (var type in SafeGetExportedTypes(assembly))
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            if (typeof(IAgentHandlerFactory).IsAssignableFrom(type))
            {
                IAgentHandlerFactory? factory;
                try
                {
                    factory = (IAgentHandlerFactory?)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Plugin '{Plugin}': failed to instantiate IAgentHandlerFactory '{Type}' — skipped.",
                        pluginName, type.FullName);
                    continue;
                }
                if (factory is null)
                {
                    continue;
                }

                if (TryRegister(factory, pluginName, registry))
                {
                    handlers.Add(factory.HandlerTypeName);
                }
            }
            else if (typeof(IAiAgent).IsAssignableFrom(type))
            {
                var typeName = type.FullName ?? type.Name;
                var factory = DefaultHandlerFactory.Create(type, typeName);
                if (TryRegister(factory, pluginName, registry))
                {
                    handlers.Add(typeName);
                }
            }
        }

        if (handlers.Count == 0)
        {
            _logger.LogWarning(
                "Plugin '{Plugin}' at {Path} has no [VaisPlugin] attribute and convention scan found no IAgentHandlerFactory / IAiAgent types.",
                pluginName,
                assemblyPath);
            return;
        }

        registry.RecordPlugin(new PluginDescriptor(
            Name: pluginName,
            AssemblyPath: assemblyPath,
            TargetApiVersion: _options.RuntimeAbiVersion,
            Handlers: handlers,
            LoadedViaAttribute: false,
            LoadContext: loadContext));

        _logger.LogInformation(
            "Loaded plugin '{Plugin}' via convention (no VaisPlugin attribute; handlers=[{Handlers}])",
            pluginName,
            string.Join(", ", handlers));
    }

    private bool TryRegisterHandler(Assembly assembly, string handlerTypeName, string pluginName, PluginHandlerRegistry registry)
    {
        var type = assembly.GetType(handlerTypeName);
        if (type is null)
        {
            // Retry with a simple-name scan so authors can use the class name alone.
            // If exactly one exported type matches, resolve it and hint at the full name.
            var bySimpleName = assembly.GetExportedTypes()
                .Where(t => t.Name.Equals(handlerTypeName, StringComparison.Ordinal))
                .ToArray();

            if (bySimpleName.Length == 1)
            {
                _logger.LogInformation(
                    "Plugin '{Plugin}': resolved handler '{Simple}' to '{Full}' by simple name. " +
                    "Update [VaisPlugin] to use the full CLR name to silence this message.",
                    pluginName, handlerTypeName, bySimpleName[0].FullName);
                type = bySimpleName[0];
            }
            else
            {
                _logger.LogWarning(
                    "Plugin '{Plugin}': declared handler '{Handler}' was not found in the loaded assembly. " +
                    "Use the full CLR name (e.g. '{Namespace}.{Handler}'). Skipped.",
                    pluginName, handlerTypeName, assembly.GetName().Name);
                return false;
            }
        }

        // Factory path wins over IAiAgent auto-wrap.
        var factoryInterface = type.GetInterfaces().FirstOrDefault(i => i == typeof(IAgentHandlerFactory));
        if (factoryInterface is not null)
        {
            IAgentHandlerFactory? factory;
            try
            {
                factory = (IAgentHandlerFactory?)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Plugin '{Plugin}': declared handler '{Handler}' is an IAgentHandlerFactory but failed to instantiate — skipped.",
                    pluginName, handlerTypeName);
                return false;
            }
            if (factory is null)
            {
                return false;
            }

            return TryRegister(factory, pluginName, registry);
        }

        if (typeof(IAiAgent).IsAssignableFrom(type))
        {
            var factory = DefaultHandlerFactory.Create(type, handlerTypeName);
            return TryRegister(factory, pluginName, registry);
        }

        _logger.LogWarning(
            "Plugin '{Plugin}': declared handler '{Handler}' implements neither IAgentHandlerFactory nor IAiAgent — skipped.",
            pluginName, handlerTypeName);
        return false;
    }

    private bool TryRegister(IAgentHandlerFactory factory, string pluginName, PluginHandlerRegistry registry)
    {
        try
        {
            registry.Register(factory, pluginName);
            return true;
        }
        catch (PluginLoadException ex) when (ex.Urn == PluginUrns.PluginHandlerCollision)
        {
            if (_options.FailOnHandlerCollision)
            {
                throw;
            }

            _logger.LogWarning(
                "Plugin '{Plugin}': handler '{Handler}' collides with a previously registered plugin — dropped (FailOnHandlerCollision=false).",
                pluginName, factory.HandlerTypeName);
            return false;
        }
    }

    private IEnumerable<Type> SafeGetExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            _logger.LogWarning(ex,
                "Plugin assembly '{Assembly}' had type-load failures during export scan — continuing with successfully loaded types.",
                assembly.FullName);
            return ex.Types.Where(t => t is not null)!;
        }
    }

    /// <summary>Major-version match during 0.x (so 0.18.x loads on 0.18 runtime). After v1.0 this shifts to semver-major.</summary>
    private static bool AbiMatches(string pluginVersion, string runtimeVersion)
    {
        if (string.Equals(pluginVersion, runtimeVersion, StringComparison.Ordinal))
        {
            return true;
        }

        // Accept plugin patches/minors within the same major.minor — so a 0.18-targeting plugin
        // loads on a 0.18.x runtime as long as the first two components match.
        if (TryParseMajorMinor(pluginVersion, out var pm) && TryParseMajorMinor(runtimeVersion, out var rm))
        {
            return pm == rm;
        }

        return false;
    }

    private static bool TryParseMajorMinor(string version, out (int major, int minor) majorMinor)
    {
        majorMinor = default;
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return false;
        }
        majorMinor = (major, minor);
        return true;
    }
}
