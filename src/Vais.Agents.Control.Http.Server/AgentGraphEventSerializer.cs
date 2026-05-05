// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Http;

/// <summary>
/// Maps <see cref="AgentGraphEvent"/> subtypes to SSE event names + JSON bodies for
/// the v0.19 graph streaming-invoke and resume endpoints. The SSE <c>event:</c> field
/// is the wire discriminator — body JSON carries the concrete record's fields.
/// </summary>
internal static class AgentGraphEventSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Map a concrete <see cref="AgentGraphEvent"/> to its SSE event name + JSON body.</summary>
    public static (string EventName, string DataJson) Serialize(AgentGraphEvent evt)
    {
        return evt switch
        {
            GraphStarted gs      => ("graph.started",     JsonSerializer.Serialize(gs, JsonOptions)),
            NodeStarted ns       => ("node.started",      JsonSerializer.Serialize(ns, JsonOptions)),
            NodeCompleted nc     => ("node.completed",    JsonSerializer.Serialize(nc, JsonOptions)),
            EdgeTraversed et     => ("edge.traversed",    JsonSerializer.Serialize(et, JsonOptions)),
            StateUpdated su      => ("state.updated",     JsonSerializer.Serialize(su, JsonOptions)),
            NodeAgentInvoked nai => ("node.agent_invoked",  JsonSerializer.Serialize(nai, JsonOptions)),
            GraphInterrupted gi  => ("graph.interrupted", JsonSerializer.Serialize(gi, JsonOptions)),
            GraphResumed gr      => ("graph.resumed",     JsonSerializer.Serialize(gr, JsonOptions)),
            GraphCompleted gc    => ("graph.completed",   JsonSerializer.Serialize(gc, JsonOptions)),
            GraphFailed gf       => ("graph.failed",      JsonSerializer.Serialize(gf, JsonOptions)),
            _ => throw new ArgumentException(
                $"Unsupported AgentGraphEvent subtype '{evt.GetType().Name}'. AgentGraphEvent is a closed hierarchy; " +
                "add the new subtype to AgentGraphEventSerializer when extending the taxonomy.",
                nameof(evt)),
        };
    }
}
