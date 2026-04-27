// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Declarative reference to one middleware layer in a gateway config pipeline.
/// <see cref="Name"/> is resolved at agent activation time via
/// <see cref="ILlmGatewayMiddlewareFactory"/> or <see cref="IToolGatewayMiddlewareFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>"ToolWorkspacePolicy"</c> name is a positional sentinel for
/// <see cref="McpGatewayConfigManifest.WorkspacePolicies"/>-driven enforcement.
/// Its position in the list controls where workspace filtering runs in the chain;
/// the factory reads workspace policy data from the manifest rather than from
/// <see cref="Params"/>.
/// </para>
/// </remarks>
/// <param name="Name">Registered middleware name — e.g. "Prometheus", "Fallback", "ToolRateLimit".</param>
/// <param name="Params">Optional JSON params forwarded to the factory verbatim.</param>
public sealed record GatewayMiddlewareSpec(string Name, JsonElement? Params = null);
