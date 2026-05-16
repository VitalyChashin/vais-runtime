// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Maps <see cref="AgentEvent"/> subtypes to SSE event names + JSON bodies for
/// the v0.12 streaming-invoke endpoint. The SSE <c>event:</c> field IS the wire
/// discriminator — body JSON carries the concrete record's fields with no
/// type-discriminator property.
/// </summary>
internal static class AgentEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Map a concrete <see cref="AgentEvent"/> to its SSE event name + JSON body.</summary>
    public static (string EventName, string DataJson) Serialize(AgentEvent evt)
    {
        return evt switch
        {
            TurnStarted ts         => ("turn.started",        JsonSerializer.Serialize(ts, JsonOptions)),
            TurnCompleted tc       => ("turn.completed",      JsonSerializer.Serialize(tc, JsonOptions)),
            TurnFailed tf          => ("turn.failed",         JsonSerializer.Serialize(tf, JsonOptions)),
            ToolCallStarted tcs    => ("tool.started",        JsonSerializer.Serialize(tcs, JsonOptions)),
            ToolCallCompleted tcc  => ("tool.completed",      JsonSerializer.Serialize(tcc, JsonOptions)),
            ToolCallReplayed tcr   => ("tool.replayed",       JsonSerializer.Serialize(tcr, JsonOptions)),
            GuardrailTriggered gt  => ("guardrail.triggered", JsonSerializer.Serialize(gt, JsonOptions)),
            InterruptRaised ir     => ("interrupt.raised",    JsonSerializer.Serialize(ir, JsonOptions)),
            HandoffRequested hr    => ("handoff.requested",   JsonSerializer.Serialize(hr, JsonOptions)),
            CompletionDelta cd     => ("delta",               JsonSerializer.Serialize(cd, JsonOptions)),
            RequestSectionsBuilt r => ("request.sections.built", JsonSerializer.Serialize(r, JsonOptions)),
            _ => throw new ArgumentException(
                $"Unsupported AgentEvent subtype '{evt.GetType().Name}'. AgentEvent is a closed hierarchy; " +
                "add the new subtype to AgentEventSerializer when extending the taxonomy.",
                nameof(evt)),
        };
    }
}
