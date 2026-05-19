// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions.Container;

/// <summary>
/// Manages the container lifecycle for a <c>host: container</c> extension.
/// Implementations provide Docker, Kubernetes, or other container runtimes.
/// The default implementation is a no-op (containers are assumed operator-managed).
/// </summary>
public interface IContainerExtensionHost
{
    /// <summary>
    /// Start the container described by <paramref name="manifest"/> and return its base URL.
    /// Returns null if the host is a no-op and the caller should use <see cref="ExtensionSpec.Image"/>
    /// and <see cref="ExtensionSpec.Port"/> to form the URL directly.
    /// </summary>
    ValueTask<Uri?> StartAsync(ExtensionManifest manifest, CancellationToken ct = default);

    /// <summary>Stop and remove the container for <paramref name="extensionId"/>. No-op when not found.</summary>
    ValueTask StopAsync(string extensionId, CancellationToken ct = default);
}

/// <summary>
/// No-op implementation of <see cref="IContainerExtensionHost"/> for environments where
/// the operator manages container lifecycle externally. Delegates URL formation to
/// <c>spec.image</c>/<c>spec.port</c>.
/// </summary>
internal sealed class NullContainerExtensionHost : IContainerExtensionHost
{
    internal static readonly NullContainerExtensionHost Instance = new();

    public ValueTask<Uri?> StartAsync(ExtensionManifest manifest, CancellationToken ct = default)
        => new((Uri?)null);

    public ValueTask StopAsync(string extensionId, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
