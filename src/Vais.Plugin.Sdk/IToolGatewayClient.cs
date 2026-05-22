// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using Vais.Agents;

namespace Vais.Plugin.Sdk;

/// <summary>
/// Gateway client for tool invocations. Pre-configured with the <c>toolGatewayUrl</c> and
/// <c>callToken</c> from the current invocation. Calls route through the
/// <c>IToolGateway</c> middleware chain on the runtime side.
/// </summary>
public interface IToolGatewayClient
{
    /// <summary>Invokes a single tool call and returns the result.</summary>
    Task<ToolResult> InvokeAsync(
        ToolCallRequest toolCall,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes all tool calls concurrently and returns a <c>role: tool</c> <see cref="ChatTurn"/>
    /// for each result, in the same order as <paramref name="toolCalls"/>.
    /// </summary>
    Task<IReadOnlyList<ChatTurn>> InvokeAllAsync(
        IReadOnlyList<ToolCallRequest> toolCalls,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of a single tool invocation.</summary>
/// <param name="ToolCallId">Correlation ID matching the originating <see cref="ToolCallRequest.CallId"/>.</param>
/// <param name="Content">String result. Structured MCP results are serialised to a string at the gateway boundary.</param>
/// <param name="IsError">When <c>true</c>, <paramref name="Content"/> carries the error description.</param>
public sealed record ToolResult(string ToolCallId, string Content, bool IsError);

internal sealed class DefaultToolGatewayClient : IToolGatewayClient
{
    private readonly HttpClient _http;
    private readonly RequestContext _context;
    private readonly string _agentId;

    internal DefaultToolGatewayClient(HttpClient http, RequestContext context, string agentId)
    {
        _http = http;
        _context = context;
        _agentId = agentId;
    }

    public async Task<ToolResult> InvokeAsync(
        ToolCallRequest toolCall,
        CancellationToken cancellationToken = default)
    {
        using var req = BuildRequest(HttpMethod.Post, "invoke", new
        {
            toolName = toolCall.ToolName,
            toolCallId = toolCall.CallId,
            arguments = toolCall.Arguments,
        });
        using var response = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 500) body = body[..500];
            throw new ToolException($"Tool gateway returned HTTP {(int)response.StatusCode}: {body}");
        }
        return await response.Content
            .ReadFromJsonAsync<ToolResult>(PluginJsonOptions.Default, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tool gateway returned empty response.");
    }

    public async Task<IReadOnlyList<ChatTurn>> InvokeAllAsync(
        IReadOnlyList<ToolCallRequest> toolCalls,
        CancellationToken cancellationToken = default)
    {
        var results = await Task.WhenAll(
            toolCalls.Select(tc => InvokeAsync(tc, cancellationToken)))
            .ConfigureAwait(false);

        return results
            .Select(r => new ChatTurn(AgentChatRole.Tool, r.Content, ToolCallId: r.ToolCallId))
            .ToList();
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
}
