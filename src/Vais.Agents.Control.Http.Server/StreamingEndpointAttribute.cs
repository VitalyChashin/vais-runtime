// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Marker metadata for endpoints that emit <c>text/event-stream</c> responses.
/// Read by <see cref="AgentControlPlaneIdempotencyMiddleware"/> + other buffering
/// middleware to skip body-buffering entirely for these routes — streaming
/// responses are fundamentally incompatible with cache-then-replay semantics.
/// </summary>
/// <remarks>
/// <para>
/// Applied to the v0.12 <c>POST /v1/agents/{id}/invoke/stream</c> endpoint via
/// <c>.WithMetadata(new StreamingEndpointAttribute())</c> in the route builder.
/// Consumers authoring their own SSE endpoints decorate them the same way to
/// opt out of the idempotency middleware + any future buffering middleware.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class StreamingEndpointAttribute : Attribute
{
}
