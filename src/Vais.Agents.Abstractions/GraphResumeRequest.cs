// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Resume input for a previously-interrupted graph run. The pair
/// (<see cref="RunId"/>, <see cref="InterruptId"/>) must match the values from the
/// originating <see cref="GraphInvocationResult"/>; a mismatch is rejected with
/// <c>GraphInterruptMismatchException</c>.
/// </summary>
/// <param name="RunId">Run identifier from the interrupted <see cref="GraphInvocationResult"/>.</param>
/// <param name="InterruptId">Interrupt identifier from <see cref="GraphInvocationResult.PendingInterruptId"/>.</param>
/// <param name="ResumePayload">
/// Optional data the resume caller wants to inject into graph state at the resume
/// node. The orchestrator merges this into the checkpoint state before re-entering
/// the graph. <see langword="null"/> means resume with checkpoint state unchanged.
/// </param>
/// <param name="Metadata">
/// Additional caller-supplied key/value pairs merged into <see cref="AgentContext"/>
/// for the resumed portion of the run.
/// </param>
public sealed record GraphResumeRequest(
    string RunId,
    string InterruptId,
    JsonElement? ResumePayload = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
