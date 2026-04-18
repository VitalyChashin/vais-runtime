// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Result of dispatching a <see cref="ToolCallRequest"/>. Carries either a successful
/// tool result string or a formatted error that gets fed back to the model so it can
/// adjust its plan.
/// </summary>
/// <param name="CallId">The id from the originating <see cref="ToolCallRequest.CallId"/>; preserved end-to-end.</param>
/// <param name="Result">Tool output as a string. When <paramref name="Error"/> is non-null, this holds the formatted error message for the model to read.</param>
/// <param name="Error">Error type name when the tool threw. Null on success. Observability-only — the model sees the formatted <paramref name="Result"/>, not this field.</param>
public sealed record ToolCallOutcome(string CallId, string Result, string? Error = null);
