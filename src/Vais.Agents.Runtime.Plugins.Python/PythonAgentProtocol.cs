// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Wire types for the <c>vais/agent.invoke</c> and <c>vais/agent.reset</c>
/// JSON-RPC methods sent over the MCP stdio channel (v0.24).
/// Internal; shape may change between previews.
/// </summary>
internal sealed record AgentInvokeRequest(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("userMessage")] string UserMessage,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("timeoutSeconds")] int TimeoutSeconds,
    [property: JsonPropertyName("context")] IReadOnlyDictionary<string, string>? Context);

internal sealed record AgentInvokeResponse(
    [property: JsonPropertyName("assistantMessage")] string AssistantMessage,
    [property: JsonPropertyName("newState")] string? NewState,
    [property: JsonPropertyName("usage")] IReadOnlyList<AgentInvokeUsage>? Usage,
    [property: JsonPropertyName("journal")] IReadOnlyList<AgentInvokeJournalEntry>? Journal,
    [property: JsonPropertyName("deltas")] IReadOnlyList<string>? Deltas = null);

internal sealed record AgentInvokeUsage(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("inputTokens")] int InputTokens,
    [property: JsonPropertyName("outputTokens")] int OutputTokens);

internal sealed record AgentInvokeJournalEntry(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("inputJson")] string InputJson,
    [property: JsonPropertyName("outputJson")] string OutputJson);

internal sealed record AgentResetRequest(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("sessionId")] string SessionId);

/// <summary>Empty response for <c>vais/agent.reset</c> — server returns <c>{}</c>.</summary>
internal sealed record AgentResetResponse();

// ── Streaming (v0.26) ────────────────────────────────────────────────────────

/// <summary>
/// Internal frame yielded by <see cref="IPythonAgentChannel.StreamAgentAsync"/>.
/// Exactly one of the properties is non-null:
/// <list type="bullet">
///   <item><see cref="TextDelta"/> — a streaming text chunk from the Python agent.</item>
///   <item><see cref="FinalResponse"/> — the terminal frame, carrying the assembled response.</item>
/// </list>
/// </summary>
internal sealed record AgentStreamFrame(
    string? TextDelta,
    AgentInvokeResponse? FinalResponse);

/// <summary>Shared <see cref="JsonSerializerOptions"/> for the <c>vais/agent.*</c> wire protocol.</summary>
internal static class AgentProtocolJson
{
    /// <summary>
    /// Uses the runtime default options. All wire-record properties carry explicit
    /// <c>[JsonPropertyName]</c> attributes, so the naming convention in the options
    /// does not matter — the attributes take precedence for both serialization and
    /// deserialization.
    /// </summary>
    internal static readonly JsonSerializerOptions Options = JsonSerializerOptions.Default;
}
