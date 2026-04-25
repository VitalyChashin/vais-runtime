// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Safe, secret-free descriptor of a single remote runtime reachable from this host.
/// Contains only the URL and identity mode — credentials are never surfaced.
/// </summary>
public sealed record RemoteRuntimeEntry(string Url, string IdentityMode);

/// <summary>
/// Read access to the set of remote runtimes that this host is configured to route agent
/// invocations to. Registered in DI by <c>AddAgentRemoteInvoker</c> so that the
/// <c>GET /v1/runtimes</c> control-plane endpoint can expose topology without touching
/// per-runtime credentials.
/// </summary>
public interface IRemoteRuntimeTopology
{
    /// <summary>Returns a snapshot of all configured remote runtime entries.</summary>
    IReadOnlyList<RemoteRuntimeEntry> GetEntries();
}
