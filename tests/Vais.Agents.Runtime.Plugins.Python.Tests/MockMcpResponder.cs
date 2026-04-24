// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Minimal MCP stdio server that speaks JSON-RPC over a pair of in-memory streams.
/// Handles <c>initialize</c> (with <c>notifications/initialized</c>) and
/// <c>tools/list</c>. All other messages are silently ignored.
/// </summary>
/// <remarks>
/// MCP stdio transport uses newline-delimited JSON: each JSON-RPC message is one UTF-8 line.
/// </remarks>
internal sealed class MockMcpResponder
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly IReadOnlyList<string> _toolNames;
    private readonly string _serverName;

    internal MockMcpResponder(
        Stream input,
        Stream output,
        IReadOnlyList<string>? toolNames = null,
        string serverName = "mock-server")
    {
        _input = input;
        _output = output;
        _toolNames = toolNames ?? [];
        _serverName = serverName;
    }

    /// <summary>
    /// Reads JSON-RPC messages from <c>input</c> and writes responses to <c>output</c>
    /// until EOF or cancellation.
    /// </summary>
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

                if (method == "initialize" && hasId)
                {
                    var serverInfo = $"{{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{{\"tools\":{{}}}},\"serverInfo\":{{\"name\":\"{_serverName}\",\"version\":\"1.0\"}}}}";
                    await writer.WriteLineAsync(BuildResult(idEl, serverInfo)).ConfigureAwait(false);
                }
                else if (method == "tools/list" && hasId)
                {
                    var toolsJson = string.Join(",", _toolNames.Select(t =>
                        $"{{\"name\":\"{t}\",\"description\":\"test tool\",\"inputSchema\":{{\"type\":\"object\",\"properties\":{{}}}}}}"));
                    var result = $"{{\"tools\":[{toolsJson}]}}";
                    await writer.WriteLineAsync(BuildResult(idEl, result)).ConfigureAwait(false);
                }
                else if (method == "ping" && hasId)
                {
                    await writer.WriteLineAsync(BuildResult(idEl, "{}")).ConfigureAwait(false);
                }
                // notifications/initialized and other notifications → no response
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { } // Pipe closed by supervisor
    }

    private static string BuildResult(JsonElement idEl, string resultJson) =>
        $$"""{"jsonrpc":"2.0","id":{{idEl}},"result":{{resultJson.Trim()}}}""";
}
