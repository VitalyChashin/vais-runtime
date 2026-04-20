// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Configuration knobs for the <c>POST /v1/agents/{id}/invoke/stream</c> endpoint.
/// Bind via <c>services.Configure&lt;StreamingInvokeOptions&gt;(...)</c>; defaults
/// match the 15s heartbeat cadence documented in v0.12.
/// </summary>
public sealed class StreamingInvokeOptions
{
    /// <summary>
    /// How often the endpoint emits SSE comment lines (<c>:&#x20;heartbeat &lt;utc&gt;\n\n</c>)
    /// during long pauses between agent-emitted events. Prevents proxies from
    /// closing idle connections. Default 15s. Set to <see cref="TimeSpan.Zero"/>
    /// to disable heartbeats entirely (bare agent-event stream only).
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}
