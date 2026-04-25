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
        var handle = await response.Content.ReadFromJsonAsync<AgentGraphHandle>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return handle ?? throw new InvalidOperationException("Server returned empty body on CreateGraph.");
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
        var handle = await response.Content.ReadFromJsonAsync<AgentGraphHandle>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return handle ?? throw new InvalidOperationException("Server returned empty body on UpdateGraph.");
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
    public async Task<RuntimeListResponse> GetRemoteRuntimesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/v1/runtimes", cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<RuntimeListResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new RuntimeListResponse(Array.Empty<RuntimeInfo>());
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

    private sealed record ProblemDetailsWire(string? Type, string? Title, int? Status, string? Detail, string? Instance);
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
