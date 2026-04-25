// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Vais.Agents.Control.Http;

/// <summary>
/// <see cref="IAgentRemoteInvoker"/> implementation that routes each invocation to the
/// appropriate remote runtime via HTTP.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="HttpClient"/> per normalised <c>runtimeUrl</c> is cached for the
/// lifetime of this instance. Callers should register this as a singleton.
/// </para>
/// <para>
/// Transient failures (503, 504, 429) are retried up to two times with 500 ms and
/// 1 000 ms delays. When an <see cref="IRemoteIdentityProvider"/> is supplied, the
/// inbound bearer token is transformed before forwarding; otherwise the token is
/// passed through verbatim (v0.20 behaviour).
/// </para>
/// </remarks>
internal sealed class HttpAgentRemoteInvoker : IAgentRemoteInvoker, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan[] DefaultRetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)];

    private readonly IHttpClientFactory? _factory;
    private readonly HttpClient? _singletonClient;
    private readonly IRemoteIdentityProvider? _identityProvider;
    private readonly Func<string, RemoteRuntimeOptions?>? _optionsLookup;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public HttpAgentRemoteInvoker(IHttpClientFactory? factory = null)
        : this(factory, identityProvider: null, optionsLookup: null)
    {
    }

    public HttpAgentRemoteInvoker(
        IHttpClientFactory? factory,
        IRemoteIdentityProvider? identityProvider,
        Func<string, RemoteRuntimeOptions?>? optionsLookup = null)
    {
        _factory = factory;
        _identityProvider = identityProvider;
        _optionsLookup = optionsLookup;
    }

    // Test seam: injects a pre-configured client bypassing the keyed pool.
    internal HttpAgentRemoteInvoker(HttpClient httpClientOverride)
    {
        _singletonClient = httpClientOverride;
    }

    /// <inheritdoc />
    public async ValueTask<AgentInvocationResult> InvokeAsync(
        string runtimeUrl,
        AgentHandle handle,
        AgentInvocationRequest request,
        string? bearerToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeUrl);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var normalised = NormaliseUrl(runtimeUrl);
        var path = handle.Version is { Length: > 0 }
            ? $"/v1/agents/{Uri.EscapeDataString(handle.AgentId)}/invoke?version={Uri.EscapeDataString(handle.Version)}"
            : $"/v1/agents/{Uri.EscapeDataString(handle.AgentId)}/invoke";

        // Resolve outbound token via identity provider when configured.
        string? outboundToken = bearerToken;
        if (_identityProvider is not null)
        {
            var credential = await _identityProvider
                .AcquireOutboundTokenAsync(normalised, bearerToken, cancellationToken)
                .ConfigureAwait(false);
            outboundToken = credential.Value;
        }

        var retryDelays = _optionsLookup?.Invoke(normalised)?.RetryDelays ?? DefaultRetryDelays;

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(retryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);

            var httpRequest = BuildRequest(path, request, outboundToken);
            var client = GetOrCreateClient(normalised);

            response?.Dispose();
            response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                break;

            var status = response.StatusCode;

            // Non-retryable: throw immediately.
            if (!IsRetryable(status) || attempt == retryDelays.Length)
            {
                var detail = await TryReadDetailAsync(response, cancellationToken).ConfigureAwait(false);
                response.Dispose();
                throw new RemoteAgentInvocationException(runtimeUrl, status, detail);
            }
        }

        try
        {
            var result = await response!.Content
                .ReadFromJsonAsync<AgentInvocationResult>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return result ?? throw new InvalidOperationException($"Remote runtime at '{runtimeUrl}' returned empty body on invoke.");
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string runtimeUrl,
        AgentHandle handle,
        AgentInvocationRequest request,
        string? bearerToken,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeUrl);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(request);

        var normalised = NormaliseUrl(runtimeUrl);
        var path = handle.Version is { Length: > 0 }
            ? $"/v1/agents/{Uri.EscapeDataString(handle.AgentId)}/invoke/stream?version={Uri.EscapeDataString(handle.Version)}"
            : $"/v1/agents/{Uri.EscapeDataString(handle.AgentId)}/invoke/stream";

        string? outboundToken = bearerToken;
        if (_identityProvider is not null)
        {
            var credential = await _identityProvider
                .AcquireOutboundTokenAsync(normalised, bearerToken, cancellationToken)
                .ConfigureAwait(false);
            outboundToken = credential.Value;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(outboundToken))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", outboundToken);

        var client = GetOrCreateClient(normalised);
        using var response = await client
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await TryReadDetailAsync(response, cancellationToken).ConfigureAwait(false);
            throw new RemoteAgentInvocationException(runtimeUrl, response.StatusCode, detail);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var parser = SseParser.Create(stream, AgentSseParser.ParseEventFrame);
            await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Data is { } evt)
                    yield return evt;
            }
        }
    }

    private HttpRequestMessage BuildRequest(string path, AgentInvocationRequest request, string? bearerToken)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        if (!string.IsNullOrEmpty(bearerToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return msg;
    }

    private HttpClient GetOrCreateClient(string normalisedUrl)
    {
        if (_singletonClient is not null)
            return _singletonClient;

        return _clients.GetOrAdd(normalisedUrl, url =>
        {
            if (_factory is not null)
            {
                var client = _factory.CreateClient(nameof(HttpAgentRemoteInvoker));
                client.BaseAddress = new Uri(url);
                return client;
            }
            return new HttpClient { BaseAddress = new Uri(url) };
        });
    }

    private static string NormaliseUrl(string url) =>
        url.TrimEnd('/');

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.ServiceUnavailable
               or HttpStatusCode.GatewayTimeout
               or HttpStatusCode.TooManyRequests;

    private static async Task<string?> TryReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_factory is null)
        {
            foreach (var client in _clients.Values)
                client.Dispose();
        }
        _clients.Clear();
    }
}
