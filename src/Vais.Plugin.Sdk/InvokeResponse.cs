// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Plugin.Sdk;

/// <summary>
/// Response returned by <c>POST /v1/invoke</c> and carried as the <c>data</c> payload
/// of the terminal <c>done</c> SSE event from <c>POST /v1/stream</c>.
/// </summary>
public sealed class InvokeResponse
{
    /// <summary>Final assistant reply text. Required.</summary>
    public string AssistantMessage { get; init; } = "";

    /// <summary>Updated plugin-private state, or <c>null</c> if unchanged from the request.</summary>
    public JsonElement? OpaqueState { get; init; }

    /// <summary>Tool calls made during this invocation, in execution order.</summary>
    public IReadOnlyList<JournalEntry> Journal { get; init; } = [];

    /// <summary>Token counts for the invocation. <c>null</c> when the provider does not report usage.</summary>
    public UsageCounts? Usage { get; init; }
}

/// <summary>Record of one tool call within a single invocation.</summary>
/// <param name="ToolName">Name of the tool that was called.</param>
/// <param name="ToolCallId">Correlation ID matching the originating assistant <c>toolCalls</c> entry.</param>
/// <param name="InputJson">Serialised JSON arguments passed to the tool.</param>
/// <param name="OutputJson">Serialised JSON result returned by the tool.</param>
public sealed record JournalEntry(string ToolName, string ToolCallId, string InputJson, string OutputJson);

/// <summary>Token usage counts for an LLM call.</summary>
/// <param name="InputTokens">Prompt tokens consumed.</param>
/// <param name="OutputTokens">Completion tokens produced.</param>
/// <param name="CachedTokens">Prompt tokens served from the provider's prompt cache (0 when not reported).</param>
public sealed record UsageCounts(int InputTokens, int OutputTokens, int CachedTokens);
