// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Protocols.Mcp;

/// <summary>
/// Thrown from an MCP-backed tool when the server returns
/// <c>CallToolResponse.IsError = true</c>. <c>DefaultToolCallDispatcher</c>
/// catches this as a regular tool-throw and surfaces it on
/// <c>ToolCallOutcome.Error</c> so the agent loop feeds the failure back to
/// the model.
/// </summary>
public sealed class McpToolInvocationException : Exception
{
    /// <summary>The MCP tool name that raised the error.</summary>
    public string ToolName { get; }

    /// <summary>Construct an exception carrying the tool name + server-supplied error text.</summary>
    public McpToolInvocationException(string toolName, string message)
        : base($"MCP tool '{toolName}' returned an error: {message}")
    {
        ToolName = toolName;
    }
}
