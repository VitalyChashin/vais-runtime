// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Reloads a single plugin assembly at runtime without restarting the host.
/// Invoked by <c>PluginWatcherService</c> (v0.22 PR 2) on filesystem change,
/// or directly by operators / tests. The registry swap is atomic; callers
/// receive the old and new descriptors to coordinate grain deactivation.
/// </summary>
public interface IPluginReloader
{
    /// <summary>
    /// Reload the plugin at <paramref name="pluginPath"/>. Loads the new
    /// assembly into a fresh <see cref="PluginAssemblyLoadContext"/>, atomically
    /// swaps the handler registry, and returns the reload outcome. On failure
    /// the old descriptor is kept unchanged.
    /// </summary>
    Task<PluginReloadResult> ReloadAsync(string pluginPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unload the plugin identified by <paramref name="pluginName"/>. Removes
    /// all handler registrations and unloads the collectible
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>. Returns
    /// <see cref="PluginUnloadStatus.NotFound"/> when no plugin with that name
    /// is currently loaded.
    /// </summary>
    Task<PluginUnloadResult> UnloadAsync(string pluginName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a single <see cref="IPluginReloader.ReloadAsync"/> call.
/// </summary>
/// <param name="OldDescriptor">Descriptor for the plugin that was replaced, or <c>null</c> if this was a first-load.</param>
/// <param name="NewDescriptor">Descriptor for the newly loaded plugin, or <c>null</c> on failure.</param>
/// <param name="Status">Whether the reload succeeded or why it failed.</param>
/// <param name="FailureUrn">URN from <see cref="PluginReloadUrns"/> when <see cref="Status"/> is not <see cref="PluginReloadStatus.Success"/>.</param>
/// <param name="FailureException">Exception thrown during the reload attempt, if any.</param>
public sealed record PluginReloadResult(
    PluginDescriptor? OldDescriptor,
    PluginDescriptor? NewDescriptor,
    PluginReloadStatus Status,
    string? FailureUrn,
    Exception? FailureException);

/// <summary>
/// Observer invoked by <see cref="DefaultPluginReloader"/> after a successful
/// registry swap. Implementations handle side-effects such as draining
/// in-flight requests or deactivating affected grains. Register implementations
/// in DI via <c>IEnumerable&lt;IPluginReloadHook&gt;</c>.
/// Exceptions from hooks are logged and swallowed — the swap is already
/// committed before hooks run.
/// </summary>
public interface IPluginReloadHook
{
    /// <summary>
    /// Lower values run first. Default 0. Use to order hooks relative to each other
    /// (e.g. translator-cache invalidation = 0, grain deactivation = 100).
    /// </summary>
    int Order => 0;

    /// <summary>
    /// Called once per successful reload, after the handler registry has been
    /// atomically swapped. Implementations should complete quickly; long-running
    /// drain logic should be internally time-bounded.
    /// </summary>
    Task OnReloadedAsync(PluginReloadResult result, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a plugin reload attempt.
/// </summary>
public enum PluginReloadStatus
{
    /// <summary>New plugin loaded and registry swapped successfully.</summary>
    Success = 0,

    /// <summary>Plugin DLL could not be loaded (bad IL, missing deps, IO error). Old descriptor kept.</summary>
    LoadFailed = 1,

    /// <summary>Plugin DLL's <c>TargetApiVersion</c> does not match the runtime ABI. Old descriptor kept.</summary>
    AbiMismatch = 2,

    /// <summary>Watcher fired but the plugin did not change (debounce or spurious event). No swap performed.</summary>
    NoChange = 3,
}

/// <summary>
/// Outcome of a <see cref="IPluginReloader.UnloadAsync"/> call.
/// </summary>
/// <param name="PluginName">Name of the plugin targeted by the unload.</param>
/// <param name="RemovedDescriptor">The descriptor that was removed, or <c>null</c> when the plugin was not found.</param>
/// <param name="Status">Whether the unload succeeded or why it did not.</param>
/// <param name="FailureUrn">Optional URN from <see cref="PluginReloadUrns"/> when unload failed.</param>
public sealed record PluginUnloadResult(
    string PluginName,
    PluginDescriptor? RemovedDescriptor,
    PluginUnloadStatus Status,
    string? FailureUrn);

/// <summary>
/// Outcome categories for <see cref="IPluginReloader.UnloadAsync"/>.
/// </summary>
public enum PluginUnloadStatus
{
    /// <summary>Plugin removed from registry and ALC unloaded.</summary>
    Success = 0,

    /// <summary>No plugin with the given name was loaded — nothing to unload.</summary>
    NotFound = 1,

    /// <summary>One or more agents still reference this plugin's handler types. Reserved for future enforcement.</summary>
    AgentsStillReference = 2,
}
