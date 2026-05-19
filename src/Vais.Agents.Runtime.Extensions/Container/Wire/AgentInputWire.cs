// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions.Container.Wire;

/// <summary>
/// Canonical JSON shape for <c>agentInput</c> context sent to <c>/handlers/&lt;id&gt;/pre</c>.
/// Excludes <c>AgentInputContext.Properties</c> (IDictionary&lt;string,object?&gt; is not round-trip stable).
/// </summary>
internal sealed record AgentInputPreRequest(
    string CallId,
    AgentInputContextWire Context);

internal sealed record AgentInputContextWire(
    string AgentId,
    string? RunId,
    string? NodeId,
    string Message);

/// <summary>
/// Canonical JSON shape for the <c>agentInput</c> pre-response.
/// </summary>
internal sealed record HandlerPreResponse(
    string Action,
    string? ContinuationToken,
    IReadOnlyDictionary<string, object?>? ContextPatch);

/// <summary>
/// Canonical JSON shape sent to <c>/handlers/&lt;id&gt;/post</c> after the wrapped operation.
/// </summary>
internal sealed record AgentInputPostRequest(
    string CallId,
    string? ContinuationToken);

/// <summary>
/// Canonical JSON shape for the post-response.
/// </summary>
internal sealed record HandlerPostResponse(
    string Action,
    IReadOnlyDictionary<string, object?>? ContextPatch);
