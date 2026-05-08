// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Plugins.Container;

internal sealed class PluginInvokeRequest
{
    public string AgentId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public IReadOnlyList<PluginMessage> Messages { get; init; } = [];
    public string LlmGatewayUrl { get; init; } = "";
    public string ToolGatewayUrl { get; init; } = "";
    public JsonElement? OpaqueState { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public PluginRequestContext Context { get; init; } = new();
}

internal sealed class PluginRequestContext
{
    public string? Traceparent { get; init; }
    public string? RunId { get; init; }
    public string? CorrelationId { get; init; }
    public string CallToken { get; init; } = "";
}

internal sealed class PluginMessage
{
    public string Role { get; init; } = "";
    public string? Content { get; init; }
    public IReadOnlyList<PluginToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
}

internal sealed class PluginToolCall
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public JsonElement Arguments { get; init; }
}

internal sealed class PluginInvokeResponse
{
    public string AssistantMessage { get; init; } = "";
    public JsonElement? OpaqueState { get; init; }
    public IReadOnlyList<PluginJournalEntry>? Journal { get; init; }
    public PluginUsageCounts? Usage { get; init; }
}

internal sealed class PluginJournalEntry
{
    public string ToolName { get; init; } = "";
    public string ToolCallId { get; init; } = "";
    public string? InputJson { get; init; }
    public string? OutputJson { get; init; }
}

internal sealed class PluginUsageCounts
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int CachedTokens { get; init; }
}

internal sealed class PluginMetadataResponse
{
    public string HandlerTypeName { get; init; } = "";
    public string TargetApiVersion { get; init; } = "";
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public string? SdkVersion { get; init; }
}

internal sealed class PluginErrorResponse
{
    public string ErrorType { get; init; } = "InternalError";
    public string ErrorMessage { get; init; } = "";
    public string? DiagnosticTail { get; init; }
}

// Gateway callback request/response types (S18/S19)
internal sealed class GatewayLlmCompleteRequest
{
    public IReadOnlyList<PluginMessage> Messages { get; init; } = [];
    public string? ModelId { get; init; }
}

internal sealed class GatewayLlmCompleteResponse
{
    public PluginMessage Message { get; init; } = new();
    public PluginUsageCounts? Usage { get; init; }
}

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
