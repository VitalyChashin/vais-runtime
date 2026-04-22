// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Identity propagation mode for outbound calls to a remote runtime.
/// Configured per target via <see cref="RemoteRuntimeOptions"/>.
/// </summary>
public enum RemoteIdentityMode
{
    /// <summary>Forward the inbound bearer token verbatim (default, v0.20 behaviour).</summary>
    Forward = 0,

    /// <summary>Use a Kubernetes projected ServiceAccount token as the subject token.</summary>
    ServiceAccount = 1,

    /// <summary>Exchange the subject token for an audience-scoped token via RFC 8693.</summary>
    TokenExchange = 2,
}
