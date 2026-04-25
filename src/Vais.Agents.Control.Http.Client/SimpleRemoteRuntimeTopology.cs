// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Thin <see cref="IRemoteRuntimeTopology"/> implementation that holds a pre-built
/// list of safe entries (URL + identity mode — no secrets). Built and registered by
/// <see cref="HttpAgentRemoteInvokerServiceCollectionExtensions"/>.
/// </summary>
internal sealed class SimpleRemoteRuntimeTopology : IRemoteRuntimeTopology
{
    private readonly IReadOnlyList<RemoteRuntimeEntry> _entries;

    internal SimpleRemoteRuntimeTopology(IReadOnlyList<RemoteRuntimeEntry> entries)
        => _entries = entries;

    public IReadOnlyList<RemoteRuntimeEntry> GetEntries() => _entries;
}
