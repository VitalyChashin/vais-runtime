// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// LLM model binding for a declarative agent — which provider, which model, which
/// credentials, and which decoding parameters. Lands on <see cref="AgentManifest.Model"/>
/// in v0.6 so operators can swap models without a code change.
/// </summary>
/// <param name="Provider">
/// Provider key — e.g. <c>"openai"</c>, <c>"azureOpenAi"</c>, <c>"anthropic"</c>,
/// <c>"google"</c>, <c>"mistral"</c>, <c>"local"</c>, <c>"custom"</c>. Consumer-defined;
/// the runtime routes to the matching <c>ICompletionProvider</c> binding.
/// </param>
/// <param name="Id">Model identifier the provider understands — <c>"gpt-4.1"</c>, <c>"claude-3-7-sonnet"</c>, etc.</param>
/// <param name="ApiKeyRef">Credential pointer (<c>secret://</c> URI) resolved at activation time.</param>
/// <param name="BaseUrlRef">Optional base-URL override for Azure / proxy / local deployments.</param>
/// <param name="Temperature">Optional sampling temperature. Null = provider default.</param>
/// <param name="TopP">Optional nucleus-sampling threshold. Null = provider default.</param>
/// <param name="MaxTokens">Optional response token cap. Null = provider default.</param>
/// <param name="ResponseFormat">
/// Optional response shape — <c>"text"</c> (default), <c>"json"</c>, or <c>"structured"</c>.
/// Consumer-defined; the runtime's model provider interprets.
/// </param>
public sealed record ModelSpec(
    string Provider,
    string Id,
    string? ApiKeyRef = null,
    string? BaseUrlRef = null,
    double? Temperature = null,
    double? TopP = null,
    int? MaxTokens = null,
    string? ResponseFormat = null);
