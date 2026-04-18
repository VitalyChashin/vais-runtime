// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais2.Agents;

/// <summary>
/// The caller's response to an <see cref="AgentInterrupt"/>. Supplied to
/// <c>StatefulAiAgent.ResumeAsync</c> to continue the run.
/// </summary>
/// <param name="InterruptId">Correlates with the <see cref="AgentInterrupt.InterruptId"/> that raised the pause.</param>
/// <param name="Payload">
/// Caller-supplied JSON describing the decision — for example
/// <c>{"approved": true}</c> for an approval gate, or a structured edit to a
/// tool-call argument block. Consumers pick the shape; the library treats it
/// as opaque and hands it to the consumer resume handler.
/// </param>
public sealed record ResumeInput(string InterruptId, JsonElement Payload);
