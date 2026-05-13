// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.OpenAiCompat;

/// <summary>
/// Configuration options for the OpenAI-compatible gateway. Bound from the
/// <c>Vais:OpenAiCompat</c> config section (supports <c>appsettings.json</c> and
/// <c>Vais__OpenAiCompat__*</c> env vars).
/// </summary>
public sealed class OpenAiCompatOptions
{
    /// <summary>Configuration section name: <c>Vais:OpenAiCompat</c>.</summary>
    public const string SectionName = "Vais:OpenAiCompat";

    /// <summary>
    /// When false, <c>agent:*</c> models are excluded from <c>GET /v1/models</c> and
    /// <c>POST /v1/chat/completions</c> returns 404 for <c>agent:</c>-prefixed model IDs.
    /// Defaults to <see langword="true"/>. Override via <c>Vais__OpenAiCompat__AgentRoutingEnabled=false</c>.
    /// </summary>
    public bool AgentRoutingEnabled { get; set; } = true;

    /// <summary>
    /// When false, <c>graph:*</c> models are excluded from <c>GET /v1/models</c> and
    /// <c>POST /v1/chat/completions</c> returns 404 for <c>graph:</c>-prefixed model IDs.
    /// Defaults to <see langword="true"/>. Override via <c>Vais__OpenAiCompat__GraphRoutingEnabled=false</c>.
    /// </summary>
    public bool GraphRoutingEnabled { get; set; } = true;
}
