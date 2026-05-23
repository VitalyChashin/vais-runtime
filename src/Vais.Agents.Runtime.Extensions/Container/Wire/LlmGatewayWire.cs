// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Extensions.Container.Wire;

/// <summary>
/// Canonical JSON shape for the <c>llmGatewayMiddleware</c> seam sent to <c>/handlers/&lt;id&gt;/pre</c>.
/// Mirrors <see cref="CompletionRequest"/>. Field names serialize camelCase.
/// </summary>
/// <remarks>
/// Limitations of the container projection (the in-process C# seam has none of these):
/// <list type="bullet">
/// <item><c>tools</c> are serialized read-only (name/description/schema) for inspection; <see cref="ITool"/>
/// instances cannot round-trip, so a mutated request keeps the original runtime-bound tools.</item>
/// <item>streaming is not expressible over pre/post — container LLM extensions act on the non-streaming path only.</item>
/// <item><c>agentId</c>/<c>runId</c> are not available at the proxy (a <see cref="CompletionRequest"/> carries no
/// run identity); they are emitted empty. The handler is already scope-bound at apply time.</item>
/// </list>
/// </remarks>
internal sealed record LlmGatewayPreRequest(
    string CallId,
    LlmRequestWire Request);

internal sealed record LlmRequestWire(
    IReadOnlyList<LlmMessageWire> Messages,
    string? SystemPrompt,
    double? Temperature,
    int? MaxTokens,
    IReadOnlyList<LlmToolDeclWire>? Tools,
    LlmResponseFormatWire? ResponseFormat,
    string AgentId,
    string? RunId);

internal sealed record LlmMessageWire(
    string Role,
    string? Content,
    IReadOnlyList<LlmToolCallWire>? ToolCalls,
    string? ToolCallId);

internal sealed record LlmToolCallWire(string Id, string Name, JsonElement Arguments);

internal sealed record LlmToolDeclWire(string Name, string? Description, JsonElement ParametersSchema);

internal sealed record LlmResponseFormatWire(JsonElement Schema, string? Name, bool Strict);

internal sealed record LlmResponseWire(string Text, int? PromptTokens, int? CompletionTokens);

/// <summary>
/// <c>llmGatewayMiddleware</c> <c>/pre</c> response. <c>shortCircuit</c> returns <see cref="Response"/>
/// without calling the model; <c>mutate</c> replaces the request from <see cref="Request"/> (tools ignored);
/// any other action proceeds with the original request.
/// </summary>
internal sealed record LlmGatewayPreResponse(
    string Action,
    string? ContinuationToken,
    LlmResponseWire? Response,
    LlmRequestWire? Request);

/// <summary>
/// <c>llmGatewayMiddleware</c> <c>/post</c> request. Carries the model response so the handler can
/// observe (audit) or transform it.
/// </summary>
internal sealed record LlmGatewayPostRequest(
    string CallId,
    string? ContinuationToken,
    LlmResponseWire Response);

/// <summary>
/// <c>llmGatewayMiddleware</c> <c>/post</c> response. <c>mutate</c> replaces the response text/usage.
/// </summary>
internal sealed record LlmGatewayPostResponse(
    string Action,
    LlmResponseWire? Response);
