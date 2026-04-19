// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// The caller's response to an <see cref="AgentInterrupt"/>. Supplied to
/// <c>StatefulAiAgent.ResumeAsync</c> to continue the run.
/// </summary>
/// <remarks>
/// <para>
/// In v0.5, setting <see cref="RunId"/> to the value carried on
/// <see cref="AgentInterrupt.RunId"/> lets the tool-call dispatcher cache-replay
/// any journaled tool outcomes from the paused run — avoiding duplicated side
/// effects. When <see cref="RunId"/> is null, resume falls back to the v0.4
/// shim semantics (new turn, no cache-replay).
/// </para>
/// </remarks>
/// <param name="InterruptId">Correlates with the <see cref="AgentInterrupt.InterruptId"/> that raised the pause.</param>
/// <param name="Payload">
/// Caller-supplied JSON describing the decision — for example
/// <c>{"approved": true}</c> for an approval gate, or a structured edit to a
/// tool-call argument block. Consumers pick the shape; the library treats it
/// as opaque and hands it to the consumer resume handler.
/// </param>
public sealed record ResumeInput(string InterruptId, JsonElement Payload)
{
    /// <summary>
    /// The run this resume should re-enter. When non-null, journaled tool
    /// outcomes for this run are cache-replayed on re-dispatch; when null,
    /// resume starts a fresh run and completed tools will be invoked again.
    /// </summary>
    public string? RunId { get; init; }
}
