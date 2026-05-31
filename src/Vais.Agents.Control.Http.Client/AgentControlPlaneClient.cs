// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Default <see cref="IAgentControlPlaneClient"/> implementation — thin wrapper
/// over <see cref="HttpClient"/>. Non-success responses surface as
/// <see cref="AgentControlPlaneException"/> carrying the RFC 7807 Problem Details
/// shape the server returns, so callers can pattern-match on the type URN
/// without bespoke HTTP parsing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wiring.</b> Typically registered via <c>IHttpClientFactory</c>:
/// <c>services.AddHttpClient&lt;IAgentControlPlaneClient, AgentControlPlaneClient&gt;(c => c.BaseAddress = new(...))</c>.
/// The <see cref="HttpClient.BaseAddress"/> must point at the server root; the
/// client appends <c>/v1/...</c> paths.
/// </para>
/// </remarks>
public sealed class AgentControlPlaneClient : IAgentControlPlaneClient
{
    private const string IdempotencyHeaderName = "Idempotency-Key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly AgentControlPlaneClientOptions _options;

    /// <summary>Construct over a pre-configured <see cref="HttpClient"/> with default options.</summary>
    public AgentControlPlaneClient(HttpClient httpClient)
        : this(httpClient, new AgentControlPlaneClientOptions())
    {
    }

    /// <summary>
    /// Construct over a pre-configured <see cref="HttpClient"/> + client options.
    /// Options govern idempotency-key auto-generation + factory overrides.
    /// </summary>
    public AgentControlPlaneClient(HttpClient httpClient, AgentControlPlaneClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _http = httpClient;
        _options = options;
    }

    /// <inheritdoc />
    public Task<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
        => CreateAsync(manifest, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentHandle> CreateAsync(AgentManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/agents")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<AgentApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on Create.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentManifest>> ListAsync(string? labelPrefix = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelPrefix)) qs.Add($"labels={Uri.EscapeDataString(labelPrefix)}");
        if (limit is int l) qs.Add($"limit={l}");
        var path = qs.Count > 0 ? $"/v1/agents?{string.Join('&', qs)}" : "/v1/agents";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var list = await response.Content.ReadFromJsonAsync<AgentListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return list?.Items ?? Array.Empty<AgentManifest>();
    }

    /// <inheritdoc />
    public async Task<AgentQueryResponse?> QueryAsync(string agentId, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var path = version is null ? $"/v1/agents/{Uri.EscapeDataString(agentId)}" : $"/v1/agents/{Uri.EscapeDataString(agentId)}?version={Uri.EscapeDataString(version)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentQueryResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version = null, CancellationToken cancellationToken = default)
        => UpdateAsync(agentId, newManifest, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(newManifest);
        var path = version is null ? $"/v1/agents/{Uri.EscapeDataString(agentId)}" : $"/v1/agents/{Uri.EscapeDataString(agentId)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(newManifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<AgentApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on Update.");
    }

    /// <inheritdoc />
    public Task CancelAsync(string agentId, string? version = null, CancellationToken cancellationToken = default)
        => DeleteAsync(agentId, version, mode: "cancel", idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public Task CancelAsync(string agentId, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => DeleteAsync(agentId, version, mode: "cancel", idempotencyKey, cancellationToken);

    /// <inheritdoc />
    public Task EvictAsync(string agentId, string? version = null, CancellationToken cancellationToken = default)
        => DeleteAsync(agentId, version, mode: "evict", idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public Task EvictAsync(string agentId, string? version, string? idempotencyKey, CancellationToken cancellationToken)
        => DeleteAsync(agentId, version, mode: "evict", idempotencyKey, cancellationToken);

    private async Task DeleteAsync(string agentId, string? version, string mode, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var qs = new List<string> { $"mode={mode}" };
        if (!string.IsNullOrWhiteSpace(version)) qs.Add($"version={Uri.EscapeDataString(version)}");
        var path = $"/v1/agents/{Uri.EscapeDataString(agentId)}?{string.Join('&', qs)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version = null, CancellationToken cancellationToken = default)
        => InvokeAsync(agentId, request, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);
        var path = version is null ? $"/v1/agents/{Uri.EscapeDataString(agentId)}/invoke" : $"/v1/agents/{Uri.EscapeDataString(agentId)}/invoke?version={Uri.EscapeDataString(version)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        AttachIdempotencyKey(httpRequest, idempotencyKey);
        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<AgentInvocationResult>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Server returned empty body on Invoke.");
    }

    /// <inheritdoc />
    public Task SignalAsync(string agentId, AgentSignal signal, string? version = null, CancellationToken cancellationToken = default)
        => SignalAsync(agentId, signal, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task SignalAsync(string agentId, AgentSignal signal, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(signal);
        var path = version is null ? $"/v1/agents/{Uri.EscapeDataString(agentId)}/signal" : $"/v1/agents/{Uri.EscapeDataString(agentId)}/signal?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(signal, options: JsonOptions),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private void AttachIdempotencyKey(HttpRequestMessage request, string? explicitKey)
    {
        var effective = explicitKey
            ?? (_options.AutoGenerateIdempotencyKey
                ? (_options.IdempotencyKeyFactory?.Invoke() ?? Guid.NewGuid().ToString("N"))
                : null);
        if (!string.IsNullOrEmpty(effective))
        {
            request.Headers.TryAddWithoutValidation(IdempotencyHeaderName, effective);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string agentId,
        AgentInvocationRequest request,
        string? version,
        string? idempotencyKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Thin text-only projection over the full-events stream.
        await foreach (var evt in InvokeStreamEventsAsync(agentId, request, version, idempotencyKey, cancellationToken).ConfigureAwait(false))
        {
            if (evt is CompletionDelta d && d.TextDelta.Length > 0)
            {
                yield return d.TextDelta;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> InvokeStreamEventsAsync(
        string agentId,
        AgentInvocationRequest request,
        string? version,
        string? idempotencyKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);

        var path = version is null
            ? $"/v1/agents/{Uri.EscapeDataString(agentId)}/invoke/stream"
            : $"/v1/agents/{Uri.EscapeDataString(agentId)}/invoke/stream?version={Uri.EscapeDataString(version)}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AttachIdempotencyKey(httpRequest, idempotencyKey);

        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentControlPlaneException(
                (int)response.StatusCode,
                type: null,
                title: "Unexpected content type",
                detail: $"Expected text/event-stream, got '{contentType ?? "<none>"}'.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var parser = SseParser.Create(stream, AgentSseParser.ParseEventFrame);
            await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Data is { } evt)
                {
                    yield return evt;
                }
            }
        }
    }

    // ── Graph verbs (v0.19) ─────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<AgentGraphHandle> CreateGraphAsync(AgentGraphManifest manifest, CancellationToken cancellationToken = default)
        => CreateGraphAsync(manifest, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentGraphHandle> CreateGraphAsync(AgentGraphManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/graphs")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<AgentGraphApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on CreateGraph.");
    }

    /// <inheritdoc />
    public async Task<AgentGraphListResponse> ListGraphsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelPrefix)) qs.Add($"labels={Uri.EscapeDataString(labelPrefix)}");
        if (limit is int l) qs.Add($"limit={l}");
        if (!string.IsNullOrWhiteSpace(cursor)) qs.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var path = qs.Count > 0 ? $"/v1/graphs?{string.Join('&', qs)}" : "/v1/graphs";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var list = await response.Content.ReadFromJsonAsync<AgentGraphListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return list ?? new AgentGraphListResponse(Array.Empty<AgentGraphManifest>());
    }

    /// <inheritdoc />
    public async Task<AgentGraphQueryResponse?> QueryGraphAsync(string graphId, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        var path = version is null ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}" : $"/v1/graphs/{Uri.EscapeDataString(graphId)}?version={Uri.EscapeDataString(version)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentGraphQueryResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AgentGraphHandle> UpdateGraphAsync(string graphId, AgentGraphManifest newManifest, string? version = null, CancellationToken cancellationToken = default)
        => UpdateGraphAsync(graphId, newManifest, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<AgentGraphHandle> UpdateGraphAsync(string graphId, AgentGraphManifest newManifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentNullException.ThrowIfNull(newManifest);
        var path = version is null ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}" : $"/v1/graphs/{Uri.EscapeDataString(graphId)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(newManifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<AgentGraphApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on UpdateGraph.");
    }

    /// <inheritdoc />
    public Task EvictGraphAsync(string graphId, string? version = null, CancellationToken cancellationToken = default)
        => EvictGraphAsync(graphId, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task EvictGraphAsync(string graphId, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(version)) qs.Add($"version={Uri.EscapeDataString(version)}");
        var path = qs.Count > 0 ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}?{string.Join('&', qs)}" : $"/v1/graphs/{Uri.EscapeDataString(graphId)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<GraphInvocationResult> InvokeGraphAsync(string graphId, GraphInvocationRequest request, string? version = null, CancellationToken cancellationToken = default)
        => InvokeGraphAsync(graphId, request, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<GraphInvocationResult> InvokeGraphAsync(string graphId, GraphInvocationRequest request, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentNullException.ThrowIfNull(request);
        var path = version is null ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}/invoke" : $"/v1/graphs/{Uri.EscapeDataString(graphId)}/invoke?version={Uri.EscapeDataString(version)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        AttachIdempotencyKey(httpRequest, idempotencyKey);
        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<GraphInvocationResult>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Server returned empty body on InvokeGraph.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentGraphEvent> InvokeGraphStreamAsync(
        string graphId,
        GraphInvocationRequest request,
        string? version,
        string? idempotencyKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentNullException.ThrowIfNull(request);
        var path = version is null
            ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}/invoke/stream"
            : $"/v1/graphs/{Uri.EscapeDataString(graphId)}/invoke/stream?version={Uri.EscapeDataString(version)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AttachIdempotencyKey(httpRequest, idempotencyKey);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var parser = SseParser.Create(stream, ParseGraphEventFrame);
            await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Data is { } evt) yield return evt;
            }
        }
    }

    /// <inheritdoc />
    public Task<GraphInvocationResult> ResumeGraphAsync(string graphId, string runId, GraphResumeRequest request, string? version = null, CancellationToken cancellationToken = default)
        => ResumeGraphAsync(graphId, runId, request, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<GraphInvocationResult> ResumeGraphAsync(string graphId, string runId, GraphResumeRequest request, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(request);
        var path = version is null
            ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}/resume"
            : $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}/resume?version={Uri.EscapeDataString(version)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        AttachIdempotencyKey(httpRequest, idempotencyKey);
        using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<GraphInvocationResult>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Server returned empty body on ResumeGraph.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentGraphEvent> ResumeGraphStreamAsync(
        string graphId,
        string runId,
        GraphResumeRequest request,
        string? version,
        string? idempotencyKey,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(request);
        var path = version is null
            ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}/resume/stream"
            : $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}/resume/stream?version={Uri.EscapeDataString(version)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        AttachIdempotencyKey(httpRequest, idempotencyKey);
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var parser = SseParser.Create(stream, ParseGraphEventFrame);
            await foreach (var item in parser.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Data is { } evt) yield return evt;
            }
        }
    }

    /// <inheritdoc />
    public async Task CancelGraphRunAsync(string graphId, string runId, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(version)) qs.Add($"version={Uri.EscapeDataString(version)}");
        var path = qs.Count > 0
            ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}?{string.Join('&', qs)}"
            : $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunListResponse> ListRunsAsync(
        string graphId,
        string? status = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (since.HasValue) qs.Add($"since={Uri.EscapeDataString(since.Value.ToString("O"))}");
        if (until.HasValue) qs.Add($"until={Uri.EscapeDataString(until.Value.ToString("O"))}");
        if (limit != 20) qs.Add($"limit={limit}");
        var path = qs.Count > 0
            ? $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs?{string.Join('&', qs)}"
            : $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RunListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new RunListResponse(Array.Empty<PipelineRunDto>());
    }

    /// <inheritdoc />
    public async Task<PipelineRunDto?> GetRunAsync(string graphId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var path = $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PipelineRunDto>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrajectoryEvent>> ListTrajectoriesAsync(
        string? agent = null,
        string? run = null,
        string? concept = null,
        string? transport = null,
        string? outcome = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent)) qs.Add($"agent={Uri.EscapeDataString(agent)}");
        if (!string.IsNullOrWhiteSpace(run)) qs.Add($"run={Uri.EscapeDataString(run)}");
        if (!string.IsNullOrWhiteSpace(concept)) qs.Add($"concept={Uri.EscapeDataString(concept)}");
        if (!string.IsNullOrWhiteSpace(transport)) qs.Add($"transport={Uri.EscapeDataString(transport)}");
        if (!string.IsNullOrWhiteSpace(outcome)) qs.Add($"outcome={Uri.EscapeDataString(outcome)}");
        if (since.HasValue) qs.Add($"since={Uri.EscapeDataString(since.Value.ToString("O"))}");
        if (until.HasValue) qs.Add($"until={Uri.EscapeDataString(until.Value.ToString("O"))}");
        if (limit != 50) qs.Add($"limit={limit}");
        var path = qs.Count > 0
            ? $"/v1/trajectories?{string.Join('&', qs)}"
            : "/v1/trajectories";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<TrajectoryEvent>>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? Array.Empty<TrajectoryEvent>();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipeProposal>> ListRecipesAsync(
        string? concept = null,
        string? kind = null,
        string? status = null,
        string? risk = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(concept)) qs.Add($"concept={Uri.EscapeDataString(concept)}");
        if (!string.IsNullOrWhiteSpace(kind)) qs.Add($"kind={Uri.EscapeDataString(kind)}");
        if (!string.IsNullOrWhiteSpace(status)) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(risk)) qs.Add($"risk={Uri.EscapeDataString(risk)}");
        if (limit != 50) qs.Add($"limit={limit}");
        var path = qs.Count > 0 ? $"/v1/recipes?{string.Join('&', qs)}" : "/v1/recipes";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<RecipeProposal>>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? Array.Empty<RecipeProposal>();
    }

    /// <inheritdoc />
    public async Task<RecipeProposal?> GetRecipeAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        using var response = await _http.GetAsync($"/v1/recipes/{Uri.EscapeDataString(proposalId)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RecipeProposal>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipeProposal>> ProposeRecipesAsync(
        string? agent = null,
        string? run = null,
        string? concept = null,
        string? transport = null,
        DateTimeOffset? since = null,
        DateTimeOffset? until = null,
        CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent)) qs.Add($"agent={Uri.EscapeDataString(agent)}");
        if (!string.IsNullOrWhiteSpace(run)) qs.Add($"run={Uri.EscapeDataString(run)}");
        if (!string.IsNullOrWhiteSpace(concept)) qs.Add($"concept={Uri.EscapeDataString(concept)}");
        if (!string.IsNullOrWhiteSpace(transport)) qs.Add($"transport={Uri.EscapeDataString(transport)}");
        if (since.HasValue) qs.Add($"since={Uri.EscapeDataString(since.Value.ToString("O"))}");
        if (until.HasValue) qs.Add($"until={Uri.EscapeDataString(until.Value.ToString("O"))}");
        var path = qs.Count > 0 ? $"/v1/recipes/propose?{string.Join('&', qs)}" : "/v1/recipes/propose";
        using var response = await _http.PostAsync(path, content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<RecipeProposal>>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? Array.Empty<RecipeProposal>();
    }

    /// <inheritdoc />
    public async Task<RecipeProposal?> DecideRecipeAsync(string proposalId, bool approve, string decidedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);
        var requestBody = JsonContent.Create(new { approve, decidedBy }, options: JsonOptions);
        using var response = await _http.PostAsync($"/v1/recipes/{Uri.EscapeDataString(proposalId)}/decide", requestBody, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await DetectApprovalPendingAsync(response, cancellationToken).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentControlPlaneException((int)response.StatusCode, type: null, title: null, detail: raw);
        }
        return JsonSerializer.Deserialize<RecipeProposal>(raw, JsonOptions);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NodeExecutionDto>> GetRunNodesAsync(string graphId, string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var path = $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}/nodes";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<NodeExecutionDto>>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? Array.Empty<NodeExecutionDto>();
    }

    /// <inheritdoc />
    public async Task<NodeExecutionDto?> GetRunNodeAsync(string graphId, string runId, string nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var path = $"/v1/graphs/{Uri.EscapeDataString(graphId)}/runs/{Uri.EscapeDataString(runId)}/nodes/{Uri.EscapeDataString(nodeId)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<NodeExecutionDto>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RuntimeListResponse> GetRemoteRuntimesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/v1/runtimes", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RuntimeListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new RuntimeListResponse(Array.Empty<RuntimeInfo>());
    }

    /// <inheritdoc />
    public async Task<PluginListResponse> ListPluginsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/v1/plugins", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PluginListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new PluginListResponse(Array.Empty<PluginInfo>());
    }

    /// <inheritdoc />
    public async Task<ExtensionListResponse> ListExtensionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/v1/extensions", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ExtensionListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new ExtensionListResponse(Array.Empty<ExtensionInfo>());
    }

    /// <inheritdoc />
    public async Task<ExtensionQueryResponse?> GetExtensionAsync(
        string extensionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        using var response = await _http.GetAsync(
            $"/v1/extensions/{Uri.EscapeDataString(extensionId)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ExtensionQueryResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentExtensionChainResponse?> GetAgentExtensionsAsync(
        string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        using var response = await _http.GetAsync(
            $"/v1/agents/{Uri.EscapeDataString(agentId)}/extensions", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<AgentExtensionChainResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExtensionMetricsResponse?> GetExtensionMetricsAsync(
        string extensionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        using var response = await _http.GetAsync(
            $"/v1/extensions/{Uri.EscapeDataString(extensionId)}/metrics", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ExtensionMetricsResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PluginSourcePushResponse> PushPluginSourceAsync(
        string pluginName, Stream sourceTarGz, CancellationToken cancellationToken = default)
    {
        using var content = new StreamContent(sourceTarGz);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        using var response = await _http.PostAsync(
            $"/v1/plugins/{Uri.EscapeDataString(pluginName)}/source", content, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PluginSourcePushResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty response body from PushPluginSourceAsync.");
    }

    /// <inheritdoc />
    public async Task<PluginImageUpdateResponse> PushPluginImageAsync(
        string pluginName,
        string image,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/v1/plugins/{Uri.EscapeDataString(pluginName)}/image",
            new PluginImageUpdateRequest(image),
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content
            .ReadFromJsonAsync<PluginImageUpdateResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty response body from PushPluginImageAsync.");
    }

    /// <inheritdoc />
    public async Task<PluginDllPushResponse> PushPluginDllAsync(
        string pluginName,
        Stream dllStream,
        string contentType = "application/octet-stream",
        CancellationToken cancellationToken = default)
    {
        using var content = new StreamContent(dllStream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await _http.PostAsync(
            $"/v1/plugins/{Uri.EscapeDataString(pluginName)}/dll", content, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PluginDllPushResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty response body from PushPluginDllAsync.");
    }

    /// <inheritdoc />
    public async Task<PluginDllPushResponse> ApplyPluginAsync(
        PluginManifest manifest,
        Stream? dllStream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var form = new MultipartFormDataContent();
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        form.Add(new StringContent(manifestJson, Encoding.UTF8, "application/json"), "manifest");
        if (dllStream is not null)
        {
            var dllContent = new StreamContent(dllStream);
            dllContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(dllContent, "dll", "plugin.dll");
        }
        using var response = await _http.PostAsync("/v1/plugins", form, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PluginDllPushResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty response body from ApplyPluginAsync.");
    }

    /// <inheritdoc />
    public async Task DeletePluginAsync(string pluginName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        using var response = await _http.DeleteAsync(
            $"/v1/plugins/{Uri.EscapeDataString(pluginName)}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PluginDllPushResponse> ImportExistingPluginAsync(
        string pluginName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        using var response = await _http.PostAsync(
            $"/v1/plugins/{Uri.EscapeDataString(pluginName)}/import",
            new StringContent(string.Empty),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<PluginDllPushResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty response body from ImportExistingPluginAsync.");
    }

    /// <inheritdoc />
    public async Task<GraphValidationResult> ValidateGraphAsync(AgentGraphManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/graphs/validate")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<GraphValidationResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new GraphValidationResult(Valid: true, Array.Empty<string>());
    }

    // ── LLM gateway config verbs (GCF-13) ──────────────────────────────────────

    /// <inheritdoc />
    public Task<LlmGatewayConfigHandle> CreateLlmGatewayConfigAsync(LlmGatewayConfigManifest manifest, CancellationToken cancellationToken = default)
        => CreateLlmGatewayConfigAsync(manifest, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<LlmGatewayConfigHandle> CreateLlmGatewayConfigAsync(LlmGatewayConfigManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/llm-gateways")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<LlmGatewayConfigApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on CreateLlmGatewayConfig.");
    }

    /// <inheritdoc />
    public Task<LlmGatewayConfigHandle> UpdateLlmGatewayConfigAsync(string id, LlmGatewayConfigManifest manifest, string? version = null, CancellationToken cancellationToken = default)
        => UpdateLlmGatewayConfigAsync(id, manifest, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<LlmGatewayConfigHandle> UpdateLlmGatewayConfigAsync(string id, LlmGatewayConfigManifest manifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(manifest);
        var path = version is null ? $"/v1/llm-gateways/{Uri.EscapeDataString(id)}" : $"/v1/llm-gateways/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<LlmGatewayConfigApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on UpdateLlmGatewayConfig.");
    }

    /// <inheritdoc />
    public async Task<LlmGatewayConfigListResponse> ListLlmGatewayConfigsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelPrefix)) qs.Add($"labels={Uri.EscapeDataString(labelPrefix)}");
        if (limit is int l) qs.Add($"limit={l}");
        if (!string.IsNullOrWhiteSpace(cursor)) qs.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var path = qs.Count > 0 ? $"/v1/llm-gateways?{string.Join('&', qs)}" : "/v1/llm-gateways";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<LlmGatewayConfigListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new LlmGatewayConfigListResponse(Array.Empty<LlmGatewayConfigManifest>());
    }

    /// <inheritdoc />
    public async Task<LlmGatewayConfigQueryResponse?> QueryLlmGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/llm-gateways/{Uri.EscapeDataString(id)}" : $"/v1/llm-gateways/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<LlmGatewayConfigQueryResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EvictLlmGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/llm-gateways/{Uri.EscapeDataString(id)}" : $"/v1/llm-gateways/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LlmGatewayConfigValidationResult> ValidateLlmGatewayConfigAsync(LlmGatewayConfigManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/llm-gateways/validate")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<LlmGatewayConfigValidationResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new LlmGatewayConfigValidationResult(Valid: true, Array.Empty<string>());
    }

    // ── MCP gateway config verbs (GCF-13) ──────────────────────────────────────

    /// <inheritdoc />
    public Task<McpGatewayConfigHandle> CreateMcpGatewayConfigAsync(McpGatewayConfigManifest manifest, CancellationToken cancellationToken = default)
        => CreateMcpGatewayConfigAsync(manifest, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<McpGatewayConfigHandle> CreateMcpGatewayConfigAsync(McpGatewayConfigManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/mcp-gateways")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<McpGatewayConfigApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on CreateMcpGatewayConfig.");
    }

    /// <inheritdoc />
    public Task<McpGatewayConfigHandle> UpdateMcpGatewayConfigAsync(string id, McpGatewayConfigManifest manifest, string? version = null, CancellationToken cancellationToken = default)
        => UpdateMcpGatewayConfigAsync(id, manifest, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<McpGatewayConfigHandle> UpdateMcpGatewayConfigAsync(string id, McpGatewayConfigManifest manifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(manifest);
        var path = version is null ? $"/v1/mcp-gateways/{Uri.EscapeDataString(id)}" : $"/v1/mcp-gateways/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<McpGatewayConfigApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on UpdateMcpGatewayConfig.");
    }

    /// <inheritdoc />
    public async Task<McpGatewayConfigListResponse> ListMcpGatewayConfigsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelPrefix)) qs.Add($"labels={Uri.EscapeDataString(labelPrefix)}");
        if (limit is int l) qs.Add($"limit={l}");
        if (!string.IsNullOrWhiteSpace(cursor)) qs.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var path = qs.Count > 0 ? $"/v1/mcp-gateways?{string.Join('&', qs)}" : "/v1/mcp-gateways";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpGatewayConfigListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new McpGatewayConfigListResponse(Array.Empty<McpGatewayConfigManifest>());
    }

    /// <inheritdoc />
    public async Task<McpGatewayConfigQueryResponse?> QueryMcpGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/mcp-gateways/{Uri.EscapeDataString(id)}" : $"/v1/mcp-gateways/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpGatewayConfigQueryResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EvictMcpGatewayConfigAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/mcp-gateways/{Uri.EscapeDataString(id)}" : $"/v1/mcp-gateways/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<McpGatewayConfigValidationResult> ValidateMcpGatewayConfigAsync(McpGatewayConfigManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/mcp-gateways/validate")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpGatewayConfigValidationResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new McpGatewayConfigValidationResult(Valid: true, Array.Empty<string>());
    }

    // ── MCP server verbs (GCF-13) ───────────────────────────────────────────────

    /// <inheritdoc />
    public Task<McpServerHandle> CreateMcpServerAsync(McpServerManifest manifest, CancellationToken cancellationToken = default)
        => CreateMcpServerAsync(manifest, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<McpServerHandle> CreateMcpServerAsync(McpServerManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/mcp-servers")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<McpServerApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on CreateMcpServer.");
    }

    /// <inheritdoc />
    public Task<McpServerHandle> UpdateMcpServerAsync(string id, McpServerManifest manifest, string? version = null, CancellationToken cancellationToken = default)
        => UpdateMcpServerAsync(id, manifest, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<McpServerHandle> UpdateMcpServerAsync(string id, McpServerManifest manifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(manifest);
        var path = version is null ? $"/v1/mcp-servers/{Uri.EscapeDataString(id)}" : $"/v1/mcp-servers/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<McpServerApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on UpdateMcpServer.");
    }

    /// <inheritdoc />
    public async Task<McpServerListResponse> ListMcpServersAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelPrefix)) qs.Add($"labels={Uri.EscapeDataString(labelPrefix)}");
        if (limit is int l) qs.Add($"limit={l}");
        if (!string.IsNullOrWhiteSpace(cursor)) qs.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var path = qs.Count > 0 ? $"/v1/mcp-servers?{string.Join('&', qs)}" : "/v1/mcp-servers";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpServerListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new McpServerListResponse(Array.Empty<McpServerManifest>());
    }

    /// <inheritdoc />
    public async Task<McpServerQueryResponse?> QueryMcpServerAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/mcp-servers/{Uri.EscapeDataString(id)}" : $"/v1/mcp-servers/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpServerQueryResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EvictMcpServerAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/mcp-servers/{Uri.EscapeDataString(id)}" : $"/v1/mcp-servers/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<McpServerValidationResult> ValidateMcpServerAsync(McpServerManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/mcp-servers/validate")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<McpServerValidationResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new McpServerValidationResult(Valid: true, Array.Empty<string>());
    }

    // ── Container plugin verbs (v0.21) ──────────────────────────────────────

    /// <inheritdoc />
    public Task<ContainerPluginHandle> CreateContainerPluginAsync(ContainerPluginManifest manifest, CancellationToken cancellationToken = default)
        => CreateContainerPluginAsync(manifest, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<ContainerPluginHandle> CreateContainerPluginAsync(ContainerPluginManifest manifest, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/container-plugins")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await DetectApprovalPendingAsync(response, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<ContainerPluginApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on CreateContainerPlugin.");
    }

    /// <inheritdoc />
    public Task<ContainerPluginHandle> UpdateContainerPluginAsync(string id, ContainerPluginManifest manifest, string? version = null, CancellationToken cancellationToken = default)
        => UpdateContainerPluginAsync(id, manifest, version, idempotencyKey: null, cancellationToken);

    /// <inheritdoc />
    public async Task<ContainerPluginHandle> UpdateContainerPluginAsync(string id, ContainerPluginManifest manifest, string? version, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(manifest);
        var path = version is null ? $"/v1/container-plugins/{Uri.EscapeDataString(id)}" : $"/v1/container-plugins/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        AttachIdempotencyKey(request, idempotencyKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await DetectApprovalPendingAsync(response, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var applyResponse = await response.Content.ReadFromJsonAsync<ContainerPluginApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return applyResponse?.Handle ?? throw new InvalidOperationException("Server returned empty body on UpdateContainerPlugin.");
    }

    /// <inheritdoc />
    public async Task<ContainerPluginListResponse> ListContainerPluginsAsync(string? labelPrefix = null, int? limit = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelPrefix)) qs.Add($"labels={Uri.EscapeDataString(labelPrefix)}");
        if (limit is int l) qs.Add($"limit={l}");
        if (!string.IsNullOrWhiteSpace(cursor)) qs.Add($"cursor={Uri.EscapeDataString(cursor)}");
        var path = qs.Count > 0 ? $"/v1/container-plugins?{string.Join('&', qs)}" : "/v1/container-plugins";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ContainerPluginListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new ContainerPluginListResponse(Array.Empty<ContainerPluginManifest>());
    }

    /// <inheritdoc />
    public async Task<ContainerPluginQueryResponse?> QueryContainerPluginAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/container-plugins/{Uri.EscapeDataString(id)}" : $"/v1/container-plugins/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ContainerPluginQueryResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EvictContainerPluginAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/container-plugins/{Uri.EscapeDataString(id)}" : $"/v1/container-plugins/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContainerPluginValidationResult> ValidateContainerPluginAsync(ContainerPluginManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/container-plugins/validate")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ContainerPluginValidationResult>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new ContainerPluginValidationResult(Valid: true, Array.Empty<string>());
    }

    /// <inheritdoc />
    public async Task<EvalSuiteApplyResponse> UpsertEvalSuiteAsync(EvalSuiteManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/eval-suites")
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<EvalSuiteApplyResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Server returned empty body on UpsertEvalSuite.");
    }

    /// <inheritdoc />
    public async Task<EvalSuiteListResponse> ListEvalSuitesAsync(string? labelPrefix = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(labelPrefix)) qs.Add($"labels={Uri.EscapeDataString(labelPrefix)}");
        if (limit is int l) qs.Add($"limit={l}");
        var path = qs.Count > 0 ? $"/v1/eval-suites?{string.Join('&', qs)}" : "/v1/eval-suites";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<EvalSuiteListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new EvalSuiteListResponse(Array.Empty<EvalSuiteManifest>());
    }

    /// <inheritdoc />
    public async Task<EvalSuiteQueryResponse?> QueryEvalSuiteAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/eval-suites/{Uri.EscapeDataString(id)}" : $"/v1/eval-suites/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<EvalSuiteQueryResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EvictEvalSuiteAsync(string id, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var path = version is null ? $"/v1/eval-suites/{Uri.EscapeDataString(id)}" : $"/v1/eval-suites/{Uri.EscapeDataString(id)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EvalRunStartResponse> StartEvalRunAsync(string suiteName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteName);
        using var response = await _http.PostAsync($"/v1/eval-suites/{Uri.EscapeDataString(suiteName)}/runs", null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<EvalRunStartResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Server returned empty body on StartEvalRun.");
    }

    /// <inheritdoc />
    public async Task<EvalRunListResponse> ListEvalRunsAsync(string? suiteName = null, int limit = 50, string? source = null, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(suiteName)) qs.Add($"suite={Uri.EscapeDataString(suiteName)}");
        if (limit != 50) qs.Add($"limit={limit}");
        if (!string.IsNullOrWhiteSpace(source)) qs.Add($"source={Uri.EscapeDataString(source)}");
        var path = qs.Count > 0 ? $"/v1/eval-runs?{string.Join('&', qs)}" : "/v1/eval-runs";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<EvalRunListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new EvalRunListResponse(Array.Empty<Vais.Agents.Eval.EvalRunSummary>());
    }

    /// <inheritdoc />
    public async Task<Vais.Agents.Eval.EvalRunDetail?> GetEvalRunAsync(string evalRunId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalRunId);
        using var response = await _http.GetAsync($"/v1/eval-runs/{Uri.EscapeDataString(evalRunId)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<Vais.Agents.Eval.EvalRunDetail>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CancelEvalRunAsync(string evalRunId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalRunId);
        using var response = await _http.PostAsync($"/v1/eval-runs/{Uri.EscapeDataString(evalRunId)}/cancel", null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EvalDiffResponse?> GetEvalDiffAsync(string baseRunId, string candidateRunId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateRunId);
        var path = $"/v1/eval-runs/diff?a={Uri.EscapeDataString(baseRunId)}&b={Uri.EscapeDataString(candidateRunId)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<EvalDiffResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamEvalRunAsync(
        string evalRunId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalRunId);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/v1/eval-runs/{Uri.EscapeDataString(evalRunId)}/stream");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null
               && !cancellationToken.IsCancellationRequested)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
                yield return line[5..].TrimStart();
        }
    }

    /// <inheritdoc />
    public async Task<ExtensionApplyResponse> ApplyExtensionAsync(
        string manifestYaml,
        Stream? dllStream,
        bool acceptLatencyCost = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestYaml);
        using var form = new MultipartFormDataContent();
        var manifestContent = new StringContent(manifestYaml, Encoding.UTF8, "application/yaml");
        form.Add(manifestContent, "manifest", "manifest.yaml");
        if (dllStream is not null)
        {
            var dllContent = new StreamContent(dllStream);
            dllContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(dllContent, "dll", "extension.dll");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/extensions") { Content = form };
        if (acceptLatencyCost)
            request.Headers.TryAddWithoutValidation("X-Vais-Accept-Latency-Cost", "true");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await DetectApprovalPendingAsync(response, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ExtensionApplyResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty response body from ApplyExtensionAsync.");
    }

    /// <inheritdoc />
    public async Task DeleteExtensionAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        using var response = await _http.DeleteAsync(
            $"/v1/extensions/{Uri.EscapeDataString(extensionId)}", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DiagSpanListResponse> GetDiagSpansAsync(string? source = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(source)) qs.Add($"source={Uri.EscapeDataString(source)}");
        if (limit != 100) qs.Add($"limit={limit}");
        var path = qs.Count > 0 ? $"/v1/diagnostics/spans?{string.Join('&', qs)}" : "/v1/diagnostics/spans";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return new DiagSpanListResponse(Array.Empty<Vais.Agents.Control.DiagSpanRecord>());
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<DiagSpanListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new DiagSpanListResponse(Array.Empty<Vais.Agents.Control.DiagSpanRecord>());
    }

    /// <inheritdoc />
    public async Task<FilterStatusResponse> GetFilterStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/v1/diagnostics/filter-status", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<FilterStatusResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new FilterStatusResponse(Array.Empty<Vais.Agents.Control.FilterCallEntry>(), 0);
    }

    /// <inheritdoc />
    public async Task<RunHealthListResponse> GetRunHealthAsync(string? level = null, string? since = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(level)) qs.Add($"level={Uri.EscapeDataString(level)}");
        if (!string.IsNullOrEmpty(since)) qs.Add($"since={Uri.EscapeDataString(since)}");
        if (limit != 50) qs.Add($"limit={limit}");
        var path = qs.Count > 0 ? $"/v1/run-health?{string.Join('&', qs)}" : "/v1/run-health";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return new RunHealthListResponse(0, Array.Empty<RunHealthListItemDto>());
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RunHealthListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new RunHealthListResponse(0, Array.Empty<RunHealthListItemDto>());
    }

    /// <inheritdoc />
    public async Task<RunHealthSignalsResponse> GetRunHealthSignalsAsync(string? concept = null, string? agentName = null, string? since = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(concept)) qs.Add($"concept={Uri.EscapeDataString(concept)}");
        if (!string.IsNullOrEmpty(agentName)) qs.Add($"agentName={Uri.EscapeDataString(agentName)}");
        if (!string.IsNullOrEmpty(since)) qs.Add($"since={Uri.EscapeDataString(since)}");
        if (limit != 50) qs.Add($"limit={limit}");
        var path = qs.Count > 0 ? $"/v1/run-health/signals?{string.Join('&', qs)}" : "/v1/run-health/signals";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return new RunHealthSignalsResponse(0, Array.Empty<RunHealthSignalRowDto>());
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RunHealthSignalsResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new RunHealthSignalsResponse(0, Array.Empty<RunHealthSignalRowDto>());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApprovalRequest>> ListApprovalsAsync(string? status = null, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(status) ? "/v1/approvals" : $"/v1/approvals?status={Uri.EscapeDataString(status)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<ApprovalRequest>>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? Array.Empty<ApprovalRequest>();
    }

    /// <inheritdoc />
    public Task<ApprovalRequest?> ApproveAsync(string requestId, CancellationToken cancellationToken = default)
        => DecideApprovalAsync(requestId, "approve", cancellationToken);

    /// <inheritdoc />
    public Task<ApprovalRequest?> RejectAsync(string requestId, CancellationToken cancellationToken = default)
        => DecideApprovalAsync(requestId, "reject", cancellationToken);

    private async Task<ApprovalRequest?> DecideApprovalAsync(string requestId, string action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/approvals/{Uri.EscapeDataString(requestId)}/{action}");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<ApprovalRequest>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static AgentGraphEvent? ParseGraphEventFrame(string eventType, ReadOnlySpan<byte> data)
    {
        return eventType switch
        {
            "graph.started"     => JsonSerializer.Deserialize<GraphStarted>(data, JsonOptions),
            "node.started"      => JsonSerializer.Deserialize<NodeStarted>(data, JsonOptions),
            "node.completed"    => JsonSerializer.Deserialize<NodeCompleted>(data, JsonOptions),
            "edge.traversed"    => JsonSerializer.Deserialize<EdgeTraversed>(data, JsonOptions),
            "state.updated"     => JsonSerializer.Deserialize<StateUpdated>(data, JsonOptions),
            "graph.interrupted" => JsonSerializer.Deserialize<GraphInterrupted>(data, JsonOptions),
            "graph.resumed"     => JsonSerializer.Deserialize<GraphResumed>(data, JsonOptions),
            "graph.completed"   => JsonSerializer.Deserialize<GraphCompleted>(data, JsonOptions),
            "graph.failed"      => JsonSerializer.Deserialize<GraphFailed>(data, JsonOptions),
            _                   => null,
        };
    }

    // Mirrors ProblemDetailsMapping.ApprovalRequiredType on the server; duplicated here to avoid
    // a client→server project reference.
    private const string ApprovalRequiredType = "urn:vais-agents:approval-required";

    // Detects the 202 pending-approval shape before EnsureSuccessAsync sees the response.
    // Buffers the content first so subsequent reads on the fall-through path (EnsureSuccessAsync /
    // ReadFromJsonAsync in the caller) still work.
    //
    // Parses with JsonDocument rather than ProblemDetailsWire because the server's Problem Details
    // body for an approval hold carries both the RFC 7807 integer "status": 202 and an extension
    // "approvalStatus": "pending-approval". Earlier wire revisions named the extension "status",
    // which collided on the same JSON key and made the typed-record deserializer throw before it
    // could see the type URN. JsonDocument reads explicit fields by name and is robust to either
    // shape, so this helper stays correct even if a peer is still emitting the legacy key.
    private static async Task DetectApprovalPendingAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Accepted) return;

        await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("type", out var typeEl) ||
                typeEl.GetString() != ApprovalRequiredType) return;

            var requestId = doc.RootElement.TryGetProperty("requestId", out var ridEl) ? ridEl.GetString() ?? "" : "";
            var kind      = doc.RootElement.TryGetProperty("kind",      out var kEl)   ? kEl.GetString()   ?? "" : "";
            var name      = doc.RootElement.TryGetProperty("name",      out var nEl)   ? nEl.GetString()   ?? "" : "";
            throw new ApprovalRequiredException(kind, name, requestId);
        }
        catch (JsonException) { /* non-Problem-Details 202 — let the caller handle on the buffered body */ }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string? type = null;
        string? title = null;
        string? detail = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsWire>(JsonOptions, ct).ConfigureAwait(false);
            type = problem?.Type;
            title = problem?.Title;
            detail = problem?.Detail;
        }
        catch (JsonException) { /* server didn't emit Problem Details; fall through */ }
        catch (NotSupportedException) { /* wrong content-type; fall through */ }

        var body = detail ?? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new AgentControlPlaneException(
            statusCode: (int)response.StatusCode,
            type: type,
            title: title,
            detail: body);
    }

    private sealed record ProblemDetailsWire(string? Type, string? Title, int? Status, string? Detail, string? Instance)
    {
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extensions { get; init; }
    }
}

/// <summary>
/// Raised by <see cref="AgentControlPlaneClient"/> on any non-success HTTP
/// response. Carries the Problem Details type URN + title + detail when the
/// server returned them, or the raw response body otherwise.
/// </summary>
public sealed class AgentControlPlaneException : Exception
{
    /// <summary>Create an exception for a non-success response.</summary>
    public AgentControlPlaneException(int statusCode, string? type, string? title, string? detail)
        : base(detail ?? title ?? $"HTTP {statusCode}")
    {
        StatusCode = statusCode;
        Type = type;
        Title = title;
    }

    /// <summary>HTTP status code of the response.</summary>
    public int StatusCode { get; }

    /// <summary>RFC 7807 Problem Details type URN, when the server supplied one.</summary>
    public string? Type { get; }

    /// <summary>RFC 7807 Problem Details title, when the server supplied one.</summary>
    public string? Title { get; }
}
