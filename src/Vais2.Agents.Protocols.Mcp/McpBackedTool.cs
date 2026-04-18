// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Vais2.Agents.Protocols.Mcp;

/// <summary>
/// Adapter that bridges one <see cref="McpClientTool"/> to our <see cref="ITool"/>.
/// Internal — instantiated by <see cref="McpToolSource"/>.
/// </summary>
internal sealed class McpBackedTool : ITool
{
    private readonly IMcpClient _client;
    private readonly McpClientTool _tool;
    private readonly JsonSerializerOptions? _serializerOptions;

    public McpBackedTool(IMcpClient client, McpClientTool tool, JsonSerializerOptions? serializerOptions)
    {
        _client = client;
        _tool = tool;
        _serializerOptions = serializerOptions;
    }

    public string Name => _tool.ProtocolTool.Name;

    public string Description => _tool.ProtocolTool.Description ?? string.Empty;

    public JsonElement ParametersSchema => _tool.ProtocolTool.InputSchema;

    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var args = ToDictionary(arguments);

        var response = await _client.CallToolAsync(
            Name,
            args,
            progress: null,
            serializerOptions: _serializerOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var text = ConcatenateTextContent(response.Content);

        if (response.IsError == true)
        {
            // Surface as a thrown exception so DefaultToolCallDispatcher captures it as
            // ToolCallOutcome.Error and feeds the error back to the model — same convention
            // as any other tool-layer failure.
            throw new McpToolInvocationException(Name, text);
        }

        return text;
    }

    private static IReadOnlyDictionary<string, object?>? ToDictionary(JsonElement arguments)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            // MCP tool arguments are keyed; a non-object element means the caller
            // supplied something the server won't accept. Pass null and let the
            // server surface the error.
            return null;
        }

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in arguments.EnumerateObject())
        {
            dict[prop.Name] = prop.Value;
        }
        return dict;
    }

    private static string ConcatenateTextContent(IReadOnlyList<ModelContextProtocol.Protocol.Types.Content>? content)
    {
        if (content is null || content.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var item in content)
        {
            if (!string.Equals(item.Type, "text", StringComparison.Ordinal))
            {
                // Image / audio / resource blocks are ignored in v0.4.
                continue;
            }

            if (!string.IsNullOrEmpty(item.Text))
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(item.Text);
            }
        }
        return sb.ToString();
    }
}
