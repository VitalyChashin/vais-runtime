// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Control.Http;

/// <summary>
/// <see cref="IRemoteIdentityProvider"/> that exchanges the inbound subject token
/// for an audience-scoped access token via RFC 8693 OAuth 2.0 Token Exchange.
/// Exchanged tokens are cached in-memory with a safety margin before expiry.
/// </summary>
public sealed class OidcTokenExchangeRemoteIdentityProvider : IRemoteIdentityProvider, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly ISecretResolver _secretResolver;
    private readonly RemoteRuntimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Construct with an HTTP client for STS calls, secret resolver, options, and time provider.</summary>
    public OidcTokenExchangeRemoteIdentityProvider(
        HttpClient httpClient,
        ISecretResolver secretResolver,
        RemoteRuntimeOptions options,
        TimeProvider? timeProvider = null,
        ILogger<OidcTokenExchangeRemoteIdentityProvider>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<OutboundCredential> AcquireOutboundTokenAsync(
        string runtimeUrl,
        string? inboundBearerToken,
        CancellationToken cancellationToken = default)
    {
        var audience = _options.Audience ?? runtimeUrl;
        var cacheKey = $"{runtimeUrl}|{audience}";

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Credential;
        }

        var semaphore = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(cacheKey, out cached) && cached.ExpiresAt > now)
            {
                return cached.Credential;
            }

            var clientSecret = await _secretResolver
                .ResolveAsync(_options.ClientSecretRef!, cancellationToken)
                .ConfigureAwait(false);

            var subjectToken = inboundBearerToken ?? string.Empty;
            var token = await ExchangeTokenAsync(
                subjectToken, clientSecret, audience, cancellationToken).ConfigureAwait(false);

            var expiresAt = now + TimeSpan.FromSeconds(token.ExpiresIn) - ExpirySafetyMargin;
            var credential = new OutboundCredential("Bearer", token.AccessToken, expiresAt);

            _cache[cacheKey] = new CachedToken(credential, expiresAt);
            return credential;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<OidcTokenExchangeResponse> ExchangeTokenAsync(
        string subjectToken,
        string clientSecret,
        string audience,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder()
            .Append("grant_type=").Append(Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:token-exchange"))
            .Append("&subject_token=").Append(Uri.EscapeDataString(subjectToken))
            .Append("&subject_token_type=").Append(Uri.EscapeDataString("urn:ietf:params:oauth:token-type:access_token"))
            .Append("&client_id=").Append(Uri.EscapeDataString(_options.ClientId!))
            .Append("&client_secret=").Append(Uri.EscapeDataString(clientSecret))
            .Append("&audience=").Append(Uri.EscapeDataString(audience))
            .ToString();

        var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        _logger?.LogDebug("Exchanging token at {Endpoint} for audience {Audience}", _options.TokenExchangeEndpoint, audience);

        var response = await _httpClient.PostAsync(_options.TokenExchangeEndpoint, content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger?.LogError("Token exchange failed: {StatusCode} {Detail}", response.StatusCode, detail);
            throw new InvalidOperationException(
                $"Token exchange failed at '{_options.TokenExchangeEndpoint}': {response.StatusCode} {detail}");
        }

        var result = await response.Content
            .ReadFromJsonAsync<OidcTokenExchangeResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return result ?? throw new InvalidOperationException("Token exchange returned empty response.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var semaphore in _locks.Values)
            semaphore.Dispose();
        _locks.Clear();
        _cache.Clear();
    }

    private sealed record CachedToken(OutboundCredential Credential, DateTimeOffset ExpiresAt);
}
