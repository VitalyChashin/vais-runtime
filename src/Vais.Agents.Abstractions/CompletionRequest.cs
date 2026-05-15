// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Structured-output schema spec carried on a <see cref="CompletionRequest"/>.
/// When set, providers that support <c>response_format: json_schema</c> (e.g. OpenAI)
/// constrain the model's reply to this shape on the wire.
/// </summary>
/// <param name="Schema">Inline JSON Schema document.</param>
/// <param name="SchemaName">Optional schema name passed to the provider. Defaults to <c>"response"</c>.</param>
/// <param name="Strict">
/// When true, strict-mode schema enforcement is requested. The provider may reject
/// schemas that contain constructs it does not support in strict mode (e.g. <c>pattern</c>,
/// non-required properties, <c>additionalProperties: true</c>).
/// </param>
public sealed record ResponseFormatSpec(
    JsonElement Schema,
    string? SchemaName = null,
    bool Strict = true);

/// <summary>
/// A single-turn completion request submitted to an <see cref="ICompletionProvider"/>.
/// Stack-neutral — contains no <c>KernelArguments</c>, no <c>ChatOptions</c>, no
/// provider-specific types. The adapter is responsible for translating this shape
/// into whatever its stack requires.
/// </summary>
/// <param name="History">
/// Full conversation history up to (and including) the latest user message.
/// The caller owns history; the provider does not mutate this list.
/// </param>
/// <param name="SystemPrompt">
/// Optional system instruction. When set, the adapter is expected to prepend it
/// as a dedicated system message (implementation detail of the adapter).
/// </param>
/// <param name="Temperature">Sampling temperature hint; null means "use provider default".</param>
/// <param name="MaxTokens">Maximum output tokens hint; null means "use provider default".</param>
/// <param name="Tools">
/// Optional tools available to the model for this turn. When non-empty the adapter
/// is expected to advertise them to its underlying SDK with auto-invocation enabled,
/// so any tool calls the model emits are executed and their results fed back before
/// the final response is returned.
/// </param>
/// <param name="ResponseFormat">
/// Optional structured-output schema. When set and the provider supports it, the model
/// API enforces the shape on the wire. Null means the provider uses its default output
/// mode (typically free-form text or prompt-driven JSON).
/// </param>
public sealed record CompletionRequest(
    IReadOnlyList<ChatTurn> History,
    string? SystemPrompt = null,
    float? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyList<ITool>? Tools = null,
    ResponseFormatSpec? ResponseFormat = null);
