// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// 1 000 ms delays. The bearer token from the inbound caller is forwarded verbatim;
/// configurable identity propagation is deferred to v0.21.
/// </para>
/// </remarks>
internal sealed class HttpAgentRemoteInvoker : IAgentRemoteInvoker, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)];

    private readonly IHttpClientFactory? _factory;
    private readonly HttpClient? _singletonClient;
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public HttpAgentRemoteInvoker(IHttpClientFactory? factory = null)
    {
        _factory = factory;
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

        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RetryDelays[attempt - 1], cancellationToken).ConfigureAwait(false);

            var httpRequest = BuildRequest(path, request, bearerToken);
            var client = GetOrCreateClient(normalised);

            response?.Dispose();
            response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                break;

            var status = response.StatusCode;

            // Non-retryable: throw immediately.
            if (!IsRetryable(status) || attempt == RetryDelays.Length)
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
