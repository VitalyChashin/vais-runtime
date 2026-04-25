// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Vais.Agents.Control;

namespace Vais.Agents.Identity.Oidc;

/// <summary>
/// <see cref="IAgentIdentityProvider"/> backed by an OIDC-compliant identity provider
/// (Keycloak, Auth0, Microsoft Entra, etc.).
/// </summary>
/// <remarks>
/// <para>
/// <b>Inbound</b>: validates a JWT Bearer token extracted from
/// <see cref="AgentInvocationMetadataKeys.Authorization"/> in <see cref="AgentInvocationRequest.Metadata"/>.
/// Token signatures are verified via the JWKS discovered at
/// <c>{Authority}/.well-known/openid-configuration</c>; the key set is auto-refreshed
/// by the injected <see cref="IConfigurationManager{T}"/>.
/// Claims extracted: <c>sub</c> → <see cref="AgentPrincipal.Id"/>,
/// <c>tid</c>/<c>tenant_id</c> → <see cref="AgentPrincipal.TenantId"/>,
/// <c>scope</c>/<c>scp</c> → <see cref="AgentPrincipal.Scopes"/>.
/// </para>
/// <para>
/// <b>Outbound</b>: acquires an access token via the OAuth 2.0 <c>client_credentials</c> grant
/// using the token endpoint from OIDC discovery. The <c>credentialRef</c> argument (a
/// <c>secret://</c> URI) is resolved via <see cref="ISecretResolver"/> to obtain the
/// client secret; <see cref="OidcAgentIdentityOptions.ClientId"/> supplies the client id.
/// Tokens are cached in-memory per <c>(agentId, credentialRef)</c> with a 30-second
/// expiry safety margin.
/// </para>
/// </remarks>
public sealed class OidcAgentIdentityProvider : IAgentIdentityProvider, IDisposable
{
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly IOptions<OidcAgentIdentityOptions> _options;
    private readonly ISecretResolver _secretResolver;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>Construct the OIDC identity provider.</summary>
    public OidcAgentIdentityProvider(
        HttpClient httpClient,
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        IOptions<OidcAgentIdentityOptions> options,
        ISecretResolver secretResolver,
        TimeProvider? timeProvider = null,
        ILogger<OidcAgentIdentityProvider>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _configurationManager = configurationManager ?? throw new ArgumentNullException(nameof(configurationManager));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask<AgentPrincipal> AuthenticateInboundAsync(
        AgentInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authHeader = request.Metadata?.GetValueOrDefault(AgentInvocationMetadataKeys.Authorization);
        if (string.IsNullOrEmpty(authHeader))
            throw new UnauthorizedAccessException(
                $"Authorization metadata key '{AgentInvocationMetadataKeys.Authorization}' is missing from the invocation request.");

        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader[7..]
            : authHeader;

        if (string.IsNullOrWhiteSpace(token))
            throw new UnauthorizedAccessException("Bearer token value is empty.");

        var oidcConfig = await _configurationManager
            .GetConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);

        var opts = _options.Value;
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = opts.ValidateIssuer,
            ValidIssuer = oidcConfig.Issuer,
            ValidateAudience = opts.ValidateAudience && !string.IsNullOrEmpty(opts.Audience),
            ValidAudience = opts.Audience,
            ValidateLifetime = true,
            ClockSkew = opts.ClockSkew,
            IssuerSigningKeys = oidcConfig.SigningKeys,
            RequireSignedTokens = true,
        };

        var handler = new JsonWebTokenHandler();
        TokenValidationResult result;
        try
        {
            result = await handler.ValidateTokenAsync(token, validationParameters).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "JWT validation threw unexpectedly.");
            throw new UnauthorizedAccessException("JWT validation failed unexpectedly.", ex);
        }

        if (!result.IsValid)
        {
            _logger?.LogDebug(result.Exception, "JWT validation failed.");
            throw new UnauthorizedAccessException("JWT validation failed.", result.Exception);
        }

        return MapClaims(result.ClaimsIdentity);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Performs the OAuth 2.0 <c>client_credentials</c> grant against the token endpoint
    /// discovered via OIDC metadata. <paramref name="credentialRef"/> is a <c>secret://</c>
    /// URI resolved by <see cref="ISecretResolver"/> to obtain the client secret.
    /// </remarks>
    public async ValueTask<OutboundCredential> AcquireOutboundAsync(
        string agentId,
        string credentialRef,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentException.ThrowIfNullOrEmpty(credentialRef);

        var cacheKey = $"{agentId}|{credentialRef}";
        var now = _timeProvider.GetUtcNow();

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
            return cached.Credential;

        var semaphore = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(cacheKey, out cached) && cached.ExpiresAt > now)
                return cached.Credential;

            var clientSecret = await _secretResolver
                .ResolveAsync(credentialRef, cancellationToken)
                .ConfigureAwait(false);

            var oidcConfig = await _configurationManager
                .GetConfigurationAsync(cancellationToken)
                .ConfigureAwait(false);

            var tokenResponse = await FetchClientCredentialsAsync(
                oidcConfig.TokenEndpoint, _options.Value.ClientId, clientSecret, cancellationToken)
                .ConfigureAwait(false);

            var expiresAt = now + TimeSpan.FromSeconds(tokenResponse.ExpiresIn) - ExpirySafetyMargin;
            var credential = new OutboundCredential("Bearer", tokenResponse.AccessToken, expiresAt);
            _cache[cacheKey] = new CachedToken(credential, expiresAt);
            return credential;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var semaphore in _locks.Values)
            semaphore.Dispose();
        _locks.Clear();
        _cache.Clear();
    }

    private async Task<OidcTokenResponse> FetchClientCredentialsAsync(
        string tokenEndpoint,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder()
            .Append("grant_type=client_credentials")
            .Append("&client_id=").Append(Uri.EscapeDataString(clientId))
            .Append("&client_secret=").Append(Uri.EscapeDataString(clientSecret))
            .ToString();

        _logger?.LogDebug("Acquiring client_credentials token from {TokenEndpoint}", tokenEndpoint);

        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        var response = await _httpClient
            .PostAsync(tokenEndpoint, content, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogError(
                "client_credentials request failed: {StatusCode} {Detail}", response.StatusCode, detail);
            throw new InvalidOperationException(
                $"client_credentials request to '{tokenEndpoint}' failed: {(int)response.StatusCode} {detail}");
        }

        var result = await response.Content
            .ReadFromJsonAsync<OidcTokenResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return result ?? throw new InvalidOperationException("Token endpoint returned an empty response.");
    }

    private static AgentPrincipal MapClaims(ClaimsIdentity identity)
    {
        var sub = identity.FindFirst("sub")?.Value
                  ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? throw new UnauthorizedAccessException("JWT is missing the required 'sub' claim.");

        var tenantId = identity.FindFirst("tid")?.Value
                       ?? identity.FindFirst("tenant_id")?.Value;

        var scopeClaim = identity.FindFirst("scope")?.Value
                         ?? identity.FindFirst("scp")?.Value;

        var scopes = scopeClaim is null
            ? null
            : (IReadOnlyList<string>)scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return new AgentPrincipal(sub, tenantId, scopes);
    }

    private sealed record CachedToken(OutboundCredential Credential, DateTimeOffset ExpiresAt);

    private sealed record OidcTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
