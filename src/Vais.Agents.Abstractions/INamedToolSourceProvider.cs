// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Lazy lookup bridge from a named identifier to its <see cref="IToolSource"/>.
/// Implementations are created at DI-registration time but the underlying
/// <see cref="IToolSource"/> is produced on demand — allowing tool sources whose
/// backing clients are initialized at runtime (e.g. Python plugin MCP clients
/// spawned in <c>IHostedService.StartAsync</c>) to participate in the
/// manifest-driven tool resolution path.
/// </summary>
/// <remarks>
/// Multiple implementations may be registered in the same DI container.
/// The manifest translator iterates all registered providers and uses the
/// first one that returns a non-null source for the requested name.
/// </remarks>
public interface INamedToolSourceProvider
{
    /// <summary>
    /// Returns the <see cref="IToolSource"/> for the given <paramref name="name"/>,
    /// or <see langword="null"/> when this provider does not own that name or the
    /// underlying source is not yet available (e.g. the plugin subprocess is still
    /// starting or has crashed).
    /// </summary>
    IToolSource? GetByName(string name);
}
