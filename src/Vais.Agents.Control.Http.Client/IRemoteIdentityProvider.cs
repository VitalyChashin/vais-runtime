// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Acquires outbound credentials for invoking a remote runtime.
/// Implementations handle the identity propagation mode (forward, SA token, OIDC exchange).
/// </summary>
public interface IRemoteIdentityProvider
{
    /// <summary>
    /// Acquire an outbound credential for calling the remote runtime at <paramref name="runtimeUrl"/>.
    /// The <paramref name="inboundBearerToken"/> is the subject token from the caller's request,
    /// available for forwarding or token exchange.
    /// </summary>
    /// <param name="runtimeUrl">Normalised base URL of the target runtime.</param>
    /// <param name="inboundBearerToken">Inbound bearer token from the caller. Null when the caller is unauthenticated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An <see cref="OutboundCredential"/> to use on the outbound request.</returns>
    ValueTask<OutboundCredential> AcquireOutboundTokenAsync(
        string runtimeUrl,
        string? inboundBearerToken,
        CancellationToken cancellationToken = default);
}
