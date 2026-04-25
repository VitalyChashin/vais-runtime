// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Well-known key names for <see cref="AgentInvocationRequest.Metadata"/>.
/// Kept in the core abstractions package so that callers (HTTP adapters, gRPC adapters,
/// identity providers) share a single definition without importing a provider-specific package.
/// </summary>
public static class AgentInvocationMetadataKeys
{
    /// <summary>
    /// Carries the raw Authorization header value forwarded from the inbound HTTP request
    /// (e.g., <c>"Bearer &lt;token&gt;"</c>).
    /// Identity providers read this key inside <see cref="IAgentIdentityProvider.AuthenticateInboundAsync"/>.
    /// </summary>
    public const string Authorization = "authorization";
}
