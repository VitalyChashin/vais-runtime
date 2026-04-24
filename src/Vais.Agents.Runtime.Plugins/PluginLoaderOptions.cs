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

    /// <summary>
    /// Controls whether plugin DLLs can be reloaded at runtime without
    /// restarting the host. Defaults to <see cref="ReloadPolicy.Disabled"/>
    /// to preserve v0.18 startup-only behaviour. Set to
    /// <see cref="ReloadPolicy.DrainAndSwap"/> to opt in to hot-reload.
    /// </summary>
    public ReloadPolicy ReloadPolicy { get; init; } = ReloadPolicy.Disabled;
}

/// <summary>
/// Controls the plugin hot-reload strategy.
/// </summary>
public enum ReloadPolicy
{
    /// <summary>
    /// Plugins load once at silo startup and stay for the process lifetime.
    /// No filesystem watcher is created. This is the default
    /// and matches v0.18 behaviour.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// When a plugin DLL changes on disk, the runtime drains in-flight
    /// requests to the old plugin, atomically swaps the handler registry to
    /// the new load context, then deactivates affected Orleans grains so they
    /// re-activate against the new plugin on next invoke.
    /// </summary>
    DrainAndSwap = 1,
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
