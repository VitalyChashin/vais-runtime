// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Shared SSE event-frame parser for agent streaming endpoints.
/// Used by both <see cref="AgentControlPlaneClient"/> and <see cref="HttpAgentRemoteInvoker"/>.
/// </summary>
internal static class AgentSseParser
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps an SSE event name + raw UTF-8 data to a concrete <see cref="AgentEvent"/> subtype.
    /// Returns <c>null</c> for unknown event names (forward-compat: new subtypes can be added additively).
    /// </summary>
    internal static AgentEvent? ParseEventFrame(string eventType, ReadOnlySpan<byte> data) =>
        eventType switch
        {
            "turn.started"         => JsonSerializer.Deserialize<TurnStarted>(data, JsonOptions),
            "turn.completed"       => JsonSerializer.Deserialize<TurnCompleted>(data, JsonOptions),
            "turn.failed"          => JsonSerializer.Deserialize<TurnFailed>(data, JsonOptions),
            "tool.started"         => JsonSerializer.Deserialize<ToolCallStarted>(data, JsonOptions),
            "tool.completed"       => JsonSerializer.Deserialize<ToolCallCompleted>(data, JsonOptions),
            "tool.replayed"        => JsonSerializer.Deserialize<ToolCallReplayed>(data, JsonOptions),
            "guardrail.triggered"  => JsonSerializer.Deserialize<GuardrailTriggered>(data, JsonOptions),
            "interrupt.raised"     => JsonSerializer.Deserialize<InterruptRaised>(data, JsonOptions),
            "handoff.requested"    => JsonSerializer.Deserialize<HandoffRequested>(data, JsonOptions),
            "delta"                => JsonSerializer.Deserialize<CompletionDelta>(data, JsonOptions),
            "request.sections.built" => JsonSerializer.Deserialize<RequestSectionsBuilt>(data, JsonOptions),
            _                      => null,
        };
}
