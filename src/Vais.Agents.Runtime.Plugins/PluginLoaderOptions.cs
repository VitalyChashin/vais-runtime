// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Options for <see cref="AssemblyPluginLoader"/>. All fields are optional;
/// defaults target the Runtime.Host convention of
/// <c>/var/lib/vais/plugins/*/</c> with strict ABI enforcement.
/// </summary>
public sealed class PluginLoaderOptions
{
    /// <summary>
    /// Current runtime ABI major version (e.g. <c>"0.18"</c>). Plugins with
    /// a different <see cref="VaisPluginAttribute.TargetApiVersion"/> are
    /// rejected with <see cref="PluginUrns.PluginAbiMismatch"/>. Defaults to
    /// <see cref="VaisRuntimeAbi.CurrentVersion"/>.
    /// </summary>
    public string RuntimeAbiVersion { get; init; } = VaisRuntimeAbi.CurrentVersion;

    /// <summary>
    /// When true, plugins without a <see cref="VaisPluginAttribute"/> fall
    /// through to convention-based discovery (scan for
    /// <see cref="IAgentHandlerFactory"/> implementations + auto-wrap
    /// <see cref="IAiAgent"/> implementations). When false, only
    /// attributed plugins load. Defaults to true.
    /// </summary>
    public bool AllowConventionDiscovery { get; init; } = true;

    /// <summary>
    /// When true, a handler-name collision between two plugins throws
    /// <see cref="PluginLoadException"/> (runtime startup fails). When
    /// false, the second plugin logs a warning and its factory is dropped
    /// (first registration wins). Defaults to true.
    /// </summary>
    public bool FailOnHandlerCollision { get; init; } = true;
}

/// <summary>
/// Central constant for the runtime's current ABI major version. Loader +
/// <see cref="VaisPluginAttribute.TargetApiVersion"/> compare against this.
/// Bumped when <c>Vais.Agents.Abstractions</c> ships a breaking change.
/// </summary>
public static class VaisRuntimeAbi
{
    /// <summary>Current runtime ABI version. Major-only comparison during 0.x.</summary>
    public const string CurrentVersion = "0.18";
}
