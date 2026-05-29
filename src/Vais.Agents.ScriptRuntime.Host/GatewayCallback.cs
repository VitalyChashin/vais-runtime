// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.ScriptRuntime.Host;

/// <summary>
/// Sidecar-local mirror of the runtime's container-gateway tool-invoke wire shape
/// (<c>POST /v1/container-gateway/tools/invoke</c>). The runtime's own DTOs are internal,
/// so the sidecar carries its own copies; the camelCase wire form is identical.
/// </summary>
internal sealed class GatewayToolInvokeRequest
{
    public string ToolName { get; init; } = "";
    public JsonElement Arguments { get; init; }
    public string ToolCallId { get; init; } = "";
}

internal sealed class GatewayToolInvokeResponse
{
    public string ToolCallId { get; init; } = "";
    public string Content { get; init; } = "";
    public bool IsError { get; init; }
}

/// <summary>The script exceeded its <c>maxToolCalls</c> budget.</summary>
internal sealed class ToolCallLimitException(int limit)
    : Exception($"script exceeded its tool-call budget of {limit}");

/// <summary>The tool gateway rejected the call or returned an error result.</summary>
internal sealed class ToolGatewayException(string message) : Exception(message);
