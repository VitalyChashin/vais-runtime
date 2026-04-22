// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// <see cref="IRemoteIdentityProvider"/> that returns the inbound bearer token unchanged.
/// This is the default (v0.20) behaviour — zero-config for same-org deployments.
/// </summary>
public sealed class ForwardingRemoteIdentityProvider : IRemoteIdentityProvider
{
    /// <inheritdoc />
    public ValueTask<OutboundCredential> AcquireOutboundTokenAsync(
        string runtimeUrl,
        string? inboundBearerToken,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<OutboundCredential>(
            new OutboundCredential("Bearer", inboundBearerToken ?? string.Empty));
    }
}
