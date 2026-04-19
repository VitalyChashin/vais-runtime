// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Catalogue-style tool provider. Hosts use an <see cref="IToolSource"/> to pull
/// tools from a remote or dynamic catalogue (MCP server, A2A remote agent, plugin
/// directory) without pinning the full list at compile time. The discovered
/// <see cref="ITool"/>s are merged with directly-registered tools via an
/// aggregating <see cref="IToolRegistry"/> implementation (see
/// <c>Vais.Agents.Core.AggregatingToolRegistry</c>).
/// </summary>
/// <remarks>
/// <para>
/// Discovery is expected to be idempotent and relatively cheap. The aggregating
/// registry typically calls <see cref="DiscoverAsync"/> once at build time and
/// caches the result; implementations should assume repeated calls are possible
/// but not high-frequency.
/// </para>
/// </remarks>
public interface IToolSource
{
    /// <summary>
    /// Stream the tools this source currently exposes. Implementations may query a
    /// remote service, list a plugin directory, or enumerate an in-memory catalogue.
    /// </summary>
    IAsyncEnumerable<ITool> DiscoverAsync(CancellationToken cancellationToken = default);
}
