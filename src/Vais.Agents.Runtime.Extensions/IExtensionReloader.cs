// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Reloads a single extension at runtime without restarting the host.
/// The registry swap is atomic; the <see cref="IExtensionChainComposer"/> cache is
/// invalidated and affected grains are deactivated so the new chain takes effect.
/// </summary>
public interface IExtensionReloader
{
    /// <summary>
    /// Load (or reload) the extension described by <paramref name="manifest"/>.
    /// For <c>host: csharp</c>, reads the DLL from <paramref name="dllStream"/>,
    /// loads into a fresh <see cref="ExtensionAssemblyLoadContext"/>, atomically
    /// swaps the handler registry, and invalidates cached chains. Returns the outcome.
    /// On failure, the old descriptor is kept unchanged.
    /// </summary>
    Task<ExtensionReloadResult> ReloadAsync(
        ExtensionManifest manifest,
        Stream? dllStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unload the extension identified by <paramref name="extensionId"/>.
    /// Removes all handler registrations, unloads the collectible ALC, and
    /// invalidates affected agent chains. Returns <see cref="ExtensionUnloadStatus.NotFound"/>
    /// when no extension with that id is loaded.
    /// </summary>
    Task<ExtensionUnloadResult> UnloadAsync(string extensionId, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a single <see cref="IExtensionReloader.ReloadAsync"/> call.</summary>
public sealed record ExtensionReloadResult(
    ExtensionDescriptor? OldDescriptor,
    ExtensionDescriptor? NewDescriptor,
    ExtensionReloadStatus Status,
    string? FailureUrn,
    Exception? FailureException);

/// <summary>Outcome of a <see cref="IExtensionReloader.UnloadAsync"/> call.</summary>
public sealed record ExtensionUnloadResult(
    string ExtensionId,
    ExtensionDescriptor? RemovedDescriptor,
    ExtensionUnloadStatus Status,
    string? FailureUrn);

/// <summary>Outcome categories for <see cref="IExtensionReloader.ReloadAsync"/>.</summary>
public enum ExtensionReloadStatus
{
    /// <summary>New extension loaded and registry swapped successfully.</summary>
    Success = 0,
    /// <summary>Extension DLL could not be loaded (bad IL, missing deps, IO error). Old descriptor kept.</summary>
    LoadFailed = 1,
    /// <summary>Extension's <c>TargetApiVersion</c> does not match the runtime ABI. Old descriptor kept.</summary>
    AbiMismatch = 2,
    /// <summary>Two handlers on the same seam with the same priority detected. Apply rejected.</summary>
    PriorityConflict = 3,
}

/// <summary>Outcome categories for <see cref="IExtensionReloader.UnloadAsync"/>.</summary>
public enum ExtensionUnloadStatus
{
    /// <summary>Extension removed from registry and ALC unloaded.</summary>
    Success = 0,
    /// <summary>No extension with the given id was loaded — nothing to unload.</summary>
    NotFound = 1,
}

/// <summary>
/// Observer invoked by <see cref="DefaultExtensionReloader"/> after a successful registry swap.
/// Exceptions are logged and swallowed — the swap is already committed before hooks run.
/// </summary>
public interface IExtensionReloadHook
{
    /// <summary>Lower values run first. Default 0.</summary>
    int Order => 0;

    /// <summary>Called once per successful reload, after the handler registry has been swapped.</summary>
    Task OnReloadedAsync(ExtensionReloadResult result, CancellationToken cancellationToken = default);
}
