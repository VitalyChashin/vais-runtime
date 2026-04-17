// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

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
public sealed record CompletionRequest(
    IReadOnlyList<ChatTurn> History,
    string? SystemPrompt = null,
    float? Temperature = null,
    int? MaxTokens = null,
    IReadOnlyList<ITool>? Tools = null);
