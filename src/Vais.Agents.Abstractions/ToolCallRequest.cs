// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// A model-requested tool invocation, surfaced by <see cref="CompletionResponse.ToolCalls"/>
/// when the model wants to call a tool instead of (or alongside) producing final text.
/// <c>StatefulAiAgent</c> dispatches each request via <see cref="IToolCallDispatcher"/>
/// and feeds the resulting <see cref="ToolCallOutcome"/> back on the next round.
/// </summary>
/// <param name="ToolName">Name of the tool the model requested. Resolved by <see cref="IToolRegistry.GetByName"/>.</param>
/// <param name="Arguments">JSON arguments supplied by the model. Shape determined by the tool's <see cref="ITool.ParametersSchema"/>.</param>
/// <param name="CallId">Provider-assigned identifier for this call. Used to correlate the request with its result on the wire.</param>
public sealed record ToolCallRequest(string ToolName, JsonElement Arguments, string CallId);
