// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Construct over a pre-configured <see cref="HttpClient"/>.</summary>
    public AgentControlPlaneClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
    }

    /// <inheritdoc />
    public async Task<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var content = new StringContent(EnvelopeSerializer.Serialize(manifest), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/v1/agents", content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var handle = await response.Content.ReadFromJsonAsync<AgentHandle>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return handle ?? throw new InvalidOperationException("Server returned empty body on Create.");
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
    public async Task<AgentHandle> UpdateAsync(string agentId, AgentManifest newManifest, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(newManifest);
        var path = version is null ? $"/v1/agents/{Uri.EscapeDataString(agentId)}" : $"/v1/agents/{Uri.EscapeDataString(agentId)}?version={Uri.EscapeDataString(version)}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(EnvelopeSerializer.Serialize(newManifest), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var handle = await response.Content.ReadFromJsonAsync<AgentHandle>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return handle ?? throw new InvalidOperationException("Server returned empty body on Update.");
    }

    /// <inheritdoc />
    public Task CancelAsync(string agentId, string? version = null, CancellationToken cancellationToken = default)
        => DeleteAsync(agentId, version, mode: "cancel", cancellationToken);

    /// <inheritdoc />
    public Task EvictAsync(string agentId, string? version = null, CancellationToken cancellationToken = default)
        => DeleteAsync(agentId, version, mode: "evict", cancellationToken);

    private async Task DeleteAsync(string agentId, string? version, string mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var qs = new List<string> { $"mode={mode}" };
        if (!string.IsNullOrWhiteSpace(version)) qs.Add($"version={Uri.EscapeDataString(version)}");
        var path = $"/v1/agents/{Uri.EscapeDataString(agentId)}?{string.Join('&', qs)}";
        using var response = await _http.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AgentInvocationResult> InvokeAsync(string agentId, AgentInvocationRequest request, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);
        var path = version is null ? $"/v1/agents/{Uri.EscapeDataString(agentId)}/invoke" : $"/v1/agents/{Uri.EscapeDataString(agentId)}/invoke?version={Uri.EscapeDataString(version)}";
        using var response = await _http.PostAsJsonAsync(path, request, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<AgentInvocationResult>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("Server returned empty body on Invoke.");
    }

    /// <inheritdoc />
    public async Task SignalAsync(string agentId, AgentSignal signal, string? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(signal);
        var path = version is null ? $"/v1/agents/{Uri.EscapeDataString(agentId)}/signal" : $"/v1/agents/{Uri.EscapeDataString(agentId)}/signal?version={Uri.EscapeDataString(version)}";
        using var response = await _http.PostAsJsonAsync(path, signal, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
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
