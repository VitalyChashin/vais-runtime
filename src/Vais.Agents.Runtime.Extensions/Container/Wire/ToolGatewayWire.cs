// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Extensions.Container.Wire;

/// <summary>
/// Canonical JSON shape for <c>toolGatewayMiddleware</c> context sent to <c>/handlers/&lt;id&gt;/pre</c>.
/// Mirrors <see cref="ToolGatewayContext"/> plus the governance-relevant RCB fields off
/// <see cref="AgentContext"/>. Field names serialize camelCase (see <c>HttpContainerHandlerProxy</c>).
/// </summary>
internal sealed record ToolGatewayPreRequest(
    string CallId,
    ToolGatewayContextWire Context);

internal sealed record ToolGatewayContextWire(
    string ToolName,
    string CallId,
    JsonElement Arguments,
    string AgentId,
    string? RunId,
    string? PrivilegeLevel,
    string? WorkspaceId,
    IReadOnlyList<string>? AllowedTools);

/// <summary>
/// <c>toolGatewayMiddleware</c> <c>/pre</c> response. <c>shortCircuit</c> carries the synthesized
/// outcome (<see cref="Result"/> / <see cref="Error"/>) returned to the agent without dispatching
/// the tool — the deny / cached-result path.
/// </summary>
internal sealed record ToolGatewayPreResponse(
    string Action,
    string? ContinuationToken,
    string? Result,
    string? Error);

/// <summary>
/// <c>toolGatewayMiddleware</c> <c>/post</c> request. Carries the outcome produced by <c>next()</c>
/// so the handler can observe (audit) or transform it.
/// </summary>
internal sealed record ToolGatewayPostRequest(
    string CallId,
    string? ContinuationToken,
    string? OutcomeResult,
    string? OutcomeError);

/// <summary>
/// <c>toolGatewayMiddleware</c> <c>/post</c> response. <c>mutate</c> replaces the outcome
/// (e.g. redact or rewrite the tool result); any other action leaves it unchanged.
/// </summary>
internal sealed record ToolGatewayPostResponse(
    string Action,
    string? Result,
    string? Error);
