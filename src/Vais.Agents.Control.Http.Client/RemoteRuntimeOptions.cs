// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Per-target-runtime configuration for identity propagation, transport overrides,
/// and token caching. Bound from <c>Vais:RemoteRuntimes:&lt;url&gt;</c> in appsettings.
/// </summary>
public sealed class RemoteRuntimeOptions
{
    /// <summary>Identity propagation mode for this target runtime. Defaults to <see cref="RemoteIdentityMode.Forward"/>.</summary>
    public RemoteIdentityMode IdentityMode { get; set; } = RemoteIdentityMode.Forward;

    /// <summary>OIDC token exchange endpoint (STS <c>/token</c>). Required when <see cref="IdentityMode"/> is <see cref="RemoteIdentityMode.TokenExchange"/>.</summary>
    public Uri? TokenExchangeEndpoint { get; set; }

    /// <summary>OAuth 2.0 <c>client_id</c> for token exchange. Required when <see cref="IdentityMode"/> is <see cref="RemoteIdentityMode.TokenExchange"/>.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// <c>secret://</c> URI pointing at the OAuth 2.0 <c>client_secret</c>.
    /// Resolved at runtime via <c>ISecretResolver</c>.
    /// Required when <see cref="IdentityMode"/> is <see cref="RemoteIdentityMode.TokenExchange"/>.
    /// </summary>
    public string? ClientSecretRef { get; set; }

    /// <summary>Target <c>audience</c> claim for the exchanged token. Defaults to the runtime URL when null.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Filesystem path for the projected ServiceAccount token.
    /// Defaults to <c>/var/run/secrets/tokens/vais-runtime-token</c>.
    /// Used when <see cref="IdentityMode"/> is <see cref="RemoteIdentityMode.ServiceAccount"/>.
    /// </summary>
    public string ServiceAccountTokenPath { get; set; } = "/var/run/secrets/tokens/vais-runtime-token";

    /// <summary>Per-runtime HTTP request timeout override. Null uses the <see cref="HttpAgentRemoteInvoker"/> default.</summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>Per-runtime retry delay overrides. Null uses the <see cref="HttpAgentRemoteInvoker"/> defaults (500 ms, 1000 ms).</summary>
    public TimeSpan[]? RetryDelays { get; set; }

    /// <summary>Cache TTL for exchanged or SA tokens. Defaults to 5 minutes.</summary>
    public TimeSpan TokenCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Validates that required fields are present for the selected <see cref="IdentityMode"/>.</summary>
    /// <exception cref="InvalidOperationException">Required fields are missing.</exception>
    public void Validate()
    {
        switch (IdentityMode)
        {
            case RemoteIdentityMode.TokenExchange:
                if (TokenExchangeEndpoint is null)
                    throw new InvalidOperationException(
                        $"{nameof(TokenExchangeEndpoint)} is required when {nameof(IdentityMode)} is {nameof(RemoteIdentityMode.TokenExchange)}.");
                if (string.IsNullOrWhiteSpace(ClientId))
                    throw new InvalidOperationException(
                        $"{nameof(ClientId)} is required when {nameof(IdentityMode)} is {nameof(RemoteIdentityMode.TokenExchange)}.");
                if (string.IsNullOrWhiteSpace(ClientSecretRef))
                    throw new InvalidOperationException(
                        $"{nameof(ClientSecretRef)} is required when {nameof(IdentityMode)} is {nameof(RemoteIdentityMode.TokenExchange)}.");
                break;

            case RemoteIdentityMode.ServiceAccount:
                if (string.IsNullOrWhiteSpace(ServiceAccountTokenPath))
                    throw new InvalidOperationException(
                        $"{nameof(ServiceAccountTokenPath)} is required when {nameof(IdentityMode)} is {nameof(RemoteIdentityMode.ServiceAccount)}.");
                break;
        }
    }
}
