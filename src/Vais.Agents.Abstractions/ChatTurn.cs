// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// A single message in a conversation. Immutable, value-equal, serialisable.
/// </summary>
/// <param name="Role">Who produced this turn.</param>
/// <param name="Text">The message content. Non-null; may be empty (e.g., an assistant turn that only carries tool calls).</param>
/// <param name="ToolCalls">
/// Tool calls attached to this turn. Populated when <paramref name="Role"/> is
/// <see cref="AgentChatRole.Assistant"/> and the model requested tool invocations.
/// Ignored for other roles. Null on regular assistant text.
/// </param>
/// <param name="ToolCallId">
/// Correlation id for a tool result. Populated when <paramref name="Role"/> is
/// <see cref="AgentChatRole.Tool"/> — matches the <see cref="ToolCallRequest.CallId"/>
/// that produced the result. Null for other roles.
/// </param>
public sealed record ChatTurn(
    AgentChatRole Role,
    string Text,
    IReadOnlyList<ToolCallRequest>? ToolCalls = null,
    string? ToolCallId = null);
