// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Vais2.Agents.Protocols.Mcp;

/// <summary>
/// <see cref="IToolSource"/> adapter for a Model Context Protocol (MCP) server.
/// Retrieves the server's tools via <see cref="McpClient.ListToolsAsync(RequestOptions?, CancellationToken)"/>
/// and surfaces each as an <see cref="ITool"/>; <c>Vais2.Agents.Core.AggregatingToolRegistry</c>
/// pulls them into the agent's registry at build time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Connection lifecycle.</b> The caller owns the <see cref="McpClient"/> —
/// build it, connect it, hand it to this source. Stdio and streamable-HTTP
/// transports need the connect to happen before this source's first discovery
/// call.
/// </para>
/// <para>
/// <b>Tool-result serialization.</b> MCP tool results are a sequence of typed
/// content blocks (text, image, audio, resource). This adapter concatenates the
/// text blocks with newline separators and ignores non-text blocks. Mixed-modal
/// tool responses lose the non-text parts; document this in your agent when it
/// matters. A future release may surface richer content.
/// </para>
/// </remarks>
public sealed class McpToolSource : IToolSource
{
    private readonly McpClient _client;
    private readonly JsonSerializerOptions? _serializerOptions;

    /// <summary>Create a tool source bound to a pre-connected <see cref="McpClient"/>.</summary>
    /// <param name="client">The MCP client. Must already be connected when discovery runs.</param>
    /// <param name="serializerOptions">
    /// Optional serializer options forwarded to <c>ListToolsAsync</c> and
    /// <c>CallToolAsync</c> via a <see cref="RequestOptions"/> bag. Null uses the SDK default.
    /// </param>
    public McpToolSource(McpClient client, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _serializerOptions = serializerOptions;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ITool> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = BuildRequestOptions(_serializerOptions);
        var tools = await _client.ListToolsAsync(options, cancellationToken).ConfigureAwait(false);
        foreach (var mcpTool in tools)
        {
            yield return new McpBackedTool(_client, mcpTool, _serializerOptions);
        }
    }

    internal static RequestOptions? BuildRequestOptions(JsonSerializerOptions? serializerOptions)
        => serializerOptions is null
            ? null
            : new RequestOptions { JsonSerializerOptions = serializerOptions };
}
