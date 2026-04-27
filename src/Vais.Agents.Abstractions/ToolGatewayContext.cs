// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Carries the full call metadata for a single outbound tool dispatch.
/// Available to every <see cref="ToolGatewayMiddleware"/> in the chain.
/// </summary>
/// <param name="ToolName">The name of the tool being dispatched.</param>
/// <param name="CallId">
/// The call id from <see cref="ToolCallRequest.CallId"/>, preserved end-to-end.
/// Required by short-circuit middleware to construct a well-formed
/// <see cref="ToolCallOutcome"/> without access to the original request.
/// </param>
/// <param name="Arguments">Tool arguments as a <see cref="JsonElement"/>.</param>
/// <param name="AgentContext">
/// The ambient <see cref="Vais.Agents.AgentContext"/> for this dispatch, carrying the full
/// RCB (<see cref="AgentContext.AllowedTools"/>, <see cref="AgentContext.PrivilegeLevel"/>,
/// <see cref="AgentContext.WorkspaceId"/>).
/// </param>
public sealed record ToolGatewayContext(
    string ToolName,
    string CallId,
    JsonElement Arguments,
    AgentContext AgentContext);
