// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Vais.Agents;

namespace Vais.Plugin.Sdk;

/// <summary>
/// Gateway client for LLM completions. Pre-configured with the <c>llmGatewayUrl</c> and
/// <c>callToken</c> from the current invocation. All calls appear in the runtime's token
/// accounting and Langfuse traces (architectural principle P4).
/// </summary>
public interface ILlmGatewayClient
{
    /// <summary>Sends a completion request to the gateway and returns the full response.</summary>
    Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ChatTurn> messages,
        CompletionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a completion request and streams the response as text deltas.</summary>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        CompletionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional parameters for an LLM completion call.</summary>
public sealed class CompletionOptions
{
    /// <summary>Model identifier override. <c>null</c> uses the gateway's configured default.</summary>
    public string? ModelId { get; init; }

    /// <summary>Sampling temperature override.</summary>
    public float? Temperature { get; init; }

    /// <summary>Maximum tokens for the completion.</summary>
    public int? MaxTokens { get; init; }
}

/// <summary>Response from a successful LLM gateway completion call.</summary>
public sealed class LlmResponse
{
    /// <summary>Assistant reply text. <c>null</c> when the turn is tool-call only.</summary>
    public string? Content { get; init; }

    /// <summary>Tool calls requested by the model.</summary>
    public IReadOnlyList<ToolCallRequest> ToolCalls { get; init; } = [];

    /// <summary>Token usage for this call.</summary>
    public UsageCounts? Usage { get; init; }

    /// <summary>Converts this response to a <see cref="ChatTurn"/> for appending to the message list.</summary>
    public ChatTurn AsChatTurn() =>
        new(AgentChatRole.Assistant, Content ?? string.Empty, ToolCalls.Count > 0 ? ToolCalls : null);
}

internal sealed class DefaultLlmGatewayClient : ILlmGatewayClient
{
    private readonly HttpClient _http;
    private readonly RequestContext _context;
    private readonly string _agentId;

    internal DefaultLlmGatewayClient(HttpClient http, RequestContext context, string agentId)
    {
        _http = http;
        _context = context;
        _agentId = agentId;
    }

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ChatTurn> messages,
        CompletionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "complete", new
        {
            messages,
            modelId = options?.ModelId,
            options = options is null ? null : new { options.Temperature, options.MaxTokens },
        });
        using var response = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 500) body = body[..500];
            throw new LlmGatewayException($"LLM gateway returned HTTP {(int)response.StatusCode}: {body}");
        }
        var gateway = await response.Content
            .ReadFromJsonAsync<GatewayLlmResponse>(PluginJsonOptions.Default, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("LLM gateway returned empty response.");
        return new LlmResponse
        {
            Content = gateway.Message?.Text.Length > 0 ? gateway.Message.Text : null,
            ToolCalls = gateway.Message?.ToolCalls ?? [],
            Usage = gateway.Usage,
        };
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        CompletionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // IP-2: fall back to a single completion and yield the content as one token.
        // Full SSE streaming from the gateway is wired in IP-3.
        var response = await CompleteAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (response.Content is not null)
            yield return response.Content;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string relativeUrl, object body)
    {
        var req = new HttpRequestMessage(method, relativeUrl)
        {
            Content = JsonContent.Create(body, options: PluginJsonOptions.Default),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_context.CallToken}");
        req.Headers.TryAddWithoutValidation("X-Agent-Id", _agentId);
        if (_context.RunId is not null)
            req.Headers.TryAddWithoutValidation("X-Run-Id", _context.RunId);
        if (_context.Traceparent is not null)
            req.Headers.TryAddWithoutValidation("traceparent", _context.Traceparent);
        return req;
    }

    private sealed class GatewayLlmResponse
    {
        public ChatTurn? Message { get; init; }
        public UsageCounts? Usage { get; init; }
    }
}
