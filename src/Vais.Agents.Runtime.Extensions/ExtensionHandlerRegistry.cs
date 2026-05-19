// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Thread-safe registry of loaded extension descriptors. Supports atomic swap
/// (hot-reload) and remove (unload) operations under a semaphore.
/// </summary>
internal sealed class ExtensionHandlerRegistry
{
    private readonly ConcurrentDictionary<string, ExtensionDescriptor> _byId = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _swap = new(1, 1);

    /// <summary>
    /// Atomically replaces the descriptor for <paramref name="extensionId"/> with
    /// <paramref name="newDescriptor"/>. Returns the old descriptor (or null on first-load).
    /// </summary>
    internal async Task<ExtensionDescriptor?> SwapAsync(
        string extensionId,
        ExtensionDescriptor newDescriptor,
        CancellationToken cancellationToken = default)
    {
        await _swap.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _byId.TryGetValue(extensionId, out var old);
            _byId[extensionId] = newDescriptor;
            return old;
        }
        finally
        {
            _swap.Release();
        }
    }

    /// <summary>
    /// Atomically removes the descriptor for <paramref name="extensionId"/>.
    /// Returns true and the removed descriptor when found; false otherwise.
    /// </summary>
    internal async Task<ExtensionDescriptor?> RemoveAsync(
        string extensionId,
        CancellationToken cancellationToken = default)
    {
        await _swap.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _byId.TryRemove(extensionId, out var removed);
            return removed;
        }
        finally
        {
            _swap.Release();
        }
    }

    /// <summary>Returns a point-in-time snapshot of all registered extensions.</summary>
    internal IReadOnlyDictionary<string, ExtensionDescriptor> Snapshot()
        => new Dictionary<string, ExtensionDescriptor>(_byId, StringComparer.Ordinal);

    /// <summary>Returns all registered extension descriptors for diagnostics.</summary>
    internal IReadOnlyCollection<ExtensionDescriptor> All => _byId.Values.ToArray();
}
