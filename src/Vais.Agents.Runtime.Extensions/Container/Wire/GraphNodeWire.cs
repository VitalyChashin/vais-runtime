// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Extensions.Container.Wire;

/// <summary>
/// Canonical JSON shape for <c>graphNode</c> context sent to <c>/handlers/&lt;id&gt;/pre</c>.
/// Mirrors <see cref="GraphNodeContext"/>. Field names serialize camelCase; the <c>Input</c>
/// dictionary keys are domain state keys and are preserved verbatim.
/// </summary>
internal sealed record GraphNodePreRequest(
    string CallId,
    GraphNodeContextWire Context);

internal sealed record GraphNodeContextWire(
    string RunId,
    string NodeId,
    string NodeKind,
    string AgentId,
    int SuperStep,
    IReadOnlyDictionary<string, JsonElement> Input);

/// <summary>
/// <c>graphNode</c> <c>/pre</c> response. <c>shortCircuit</c> carries the substitute node
/// <see cref="Output"/> returned without running the node body (the cache / deny path); the
/// <c>/post</c> endpoint is NOT called.
/// </summary>
internal sealed record GraphNodePreResponse(
    string Action,
    string? ContinuationToken,
    IReadOnlyDictionary<string, JsonElement>? Output);

/// <summary>
/// <c>graphNode</c> <c>/post</c> request. Carries the output produced by <c>next()</c> (the node body)
/// so the handler can observe (audit) or transform it before it merges into graph state.
/// </summary>
internal sealed record GraphNodePostRequest(
    string CallId,
    string? ContinuationToken,
    IReadOnlyDictionary<string, JsonElement> Output);

/// <summary>
/// <c>graphNode</c> <c>/post</c> response. <c>mutate</c> replaces the node output; any other action
/// leaves it unchanged.
/// </summary>
internal sealed record GraphNodePostResponse(
    string Action,
    IReadOnlyDictionary<string, JsonElement>? Output);
