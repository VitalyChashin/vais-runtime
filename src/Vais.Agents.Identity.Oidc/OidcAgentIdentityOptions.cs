// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Identity.Oidc;

/// <summary>
/// Configuration for <see cref="OidcAgentIdentityProvider"/>.
/// </summary>
public sealed class OidcAgentIdentityOptions
{
    /// <summary>
    /// Base URL of the OIDC authority (e.g., <c>https://keycloak.example.com/realms/my-realm</c>).
    /// OIDC discovery is performed at <c>{Authority}/.well-known/openid-configuration</c>.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// OAuth 2.0 client identifier used for the <c>client_credentials</c> outbound flow.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Expected audience for inbound JWT validation.
    /// Checked only when <see cref="ValidateAudience"/> is <see langword="true"/>.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Whether to validate the <c>aud</c> claim on inbound JWTs.
    /// Defaults to <see langword="false"/>; set to <see langword="true"/> and supply
    /// <see cref="Audience"/> to enable audience enforcement.
    /// </summary>
    public bool ValidateAudience { get; set; } = false;

    /// <summary>
    /// Whether to validate the <c>iss</c> claim on inbound JWTs against the issuer
    /// returned by OIDC discovery. Defaults to <see langword="true"/>.
    /// </summary>
    public bool ValidateIssuer { get; set; } = true;

    /// <summary>
    /// Allowed clock skew when validating token expiry. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}
