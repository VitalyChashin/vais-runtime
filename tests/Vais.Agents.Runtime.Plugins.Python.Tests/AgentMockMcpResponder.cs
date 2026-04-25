// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Extends the mock MCP responder to also handle <c>vais/agent.invoke</c> and
/// <c>vais/agent.stream</c> calls.
/// Handles: <c>initialize</c>, <c>tools/list</c>, <c>ping</c>,
/// <c>vais/agent.invoke</c>, <c>vais/agent.stream</c>.
/// </summary>
internal sealed class AgentMockMcpResponder
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Func<AgentInvokeRequest, AgentInvokeResponse> _invokeHandler;

    // Optional: when set, vais/agent.stream sends one notification per chunk then a response.
    // (requestId: the JSON-RPC id string, request: parsed AgentInvokeRequest)
    // Returns (chunks, final response).
    private readonly Func<string, AgentInvokeRequest, (IEnumerable<string> Chunks, AgentInvokeResponse Response)>?
        _streamHandler;

    internal AgentMockMcpResponder(
        Stream input,
        Stream output,
        Func<AgentInvokeRequest, AgentInvokeResponse>? invokeHandler = null,
        Func<string, AgentInvokeRequest, (IEnumerable<string> Chunks, AgentInvokeResponse Response)>?
            streamHandler = null)
    {
        _input = input;
        _output = output;
        _invokeHandler = invokeHandler ?? (req => new AgentInvokeResponse(
            AssistantMessage: $"Echo: {req.UserMessage}",
            NewState: req.State,
            Usage: null,
            Journal: null));
        _streamHandler = streamHandler;
    }

    internal async Task RunAsync(CancellationToken ct = default)
    {
        using var reader = new StreamReader(_input, leaveOpen: true);
        await using var writer = new StreamWriter(_output, leaveOpen: true) { AutoFlush = true };

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonElement root;
                try { root = JsonDocument.Parse(line).RootElement; }
                catch (JsonException) { continue; }

                var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
                if (method is null)
                    continue;

                var hasId = root.TryGetProperty("id", out var idEl);
                if (!hasId)
                    continue; // notifications

                if (method == "initialize")
                {
                    const string serverInfo = "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"mock-agent-server\",\"version\":\"1.0\"}}";
                    await writer.WriteLineAsync(BuildResult(idEl, serverInfo)).ConfigureAwait(false);
                }
                else if (method == "tools/list")
                {
                    await writer.WriteLineAsync(BuildResult(idEl, "{\"tools\":[]}")).ConfigureAwait(false);
                }
                else if (method == "ping")
                {
                    await writer.WriteLineAsync(BuildResult(idEl, "{}")).ConfigureAwait(false);
                }
                else if (method == "vais/agent.invoke")
                {
                    var paramsEl = root.TryGetProperty("params", out var p) ? p : (JsonElement?)null;
                    AgentInvokeRequest? request = null;
                    if (paramsEl.HasValue)
                    {
                        try { request = paramsEl.Value.Deserialize<AgentInvokeRequest>(AgentProtocolJson.Options); }
                        catch (JsonException) { }
                    }

                    if (request is null)
                    {
                        var errJson = "{\"code\":-32602,\"message\":\"Invalid params\"}";
                        await writer.WriteLineAsync(BuildError(idEl, errJson)).ConfigureAwait(false);
                        continue;
                    }

                    var response = _invokeHandler(request);
                    var resultJson = JsonSerializer.Serialize(response, AgentProtocolJson.Options);
                    await writer.WriteLineAsync(BuildResult(idEl, resultJson)).ConfigureAwait(false);
                }
                else if (method == "vais/agent.stream")
                {
                    var paramsEl = root.TryGetProperty("params", out var p2) ? p2 : (JsonElement?)null;
                    AgentInvokeRequest? request = null;
                    if (paramsEl.HasValue)
                    {
                        try { request = paramsEl.Value.Deserialize<AgentInvokeRequest>(AgentProtocolJson.Options); }
                        catch (JsonException) { }
                    }

                    if (request is null)
                    {
                        var errJson = "{\"code\":-32602,\"message\":\"Invalid params\"}";
                        await writer.WriteLineAsync(BuildError(idEl, errJson)).ConfigureAwait(false);
                        continue;
                    }

                    var requestIdStr = idEl.ValueKind == JsonValueKind.String
                        ? idEl.GetString()!
                        : idEl.GetRawText();

                    IEnumerable<string> chunks;
                    AgentInvokeResponse streamResponse;
                    if (_streamHandler is not null)
                    {
                        (chunks, streamResponse) = _streamHandler(requestIdStr, request);
                    }
                    else
                    {
                        var fallback = _invokeHandler(request);
                        chunks = [fallback.AssistantMessage];
                        streamResponse = fallback;
                    }

                    var chunkList = chunks as IReadOnlyList<string> ?? new List<string>(chunks);
                    streamResponse = streamResponse with { Deltas = chunkList };
                    var streamResultJson = JsonSerializer.Serialize(streamResponse, AgentProtocolJson.Options);
                    await writer.WriteLineAsync(BuildResult(idEl, streamResultJson)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static string BuildResult(JsonElement idEl, string resultJson) =>
        $$"""{"jsonrpc":"2.0","id":{{idEl.GetRawText()}},"result":{{resultJson.Trim()}}}""";

    private static string BuildError(JsonElement idEl, string errorJson) =>
        $$"""{"jsonrpc":"2.0","id":{{idEl.GetRawText()}},"error":{{errorJson.Trim()}}}""";
}
