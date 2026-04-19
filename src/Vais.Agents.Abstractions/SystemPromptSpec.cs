// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Three shapes for supplying a system prompt to a declarative agent. Exactly one of
/// <see cref="Inline"/>, <see cref="TemplateRef"/>, <see cref="FileRef"/> must be set.
/// The manifest loader enforces the exactly-one rule; the runtime resolves the
/// chosen shape into a concrete string at agent activation time.
/// </summary>
/// <param name="Inline">Literal prompt text. Use for small / agent-specific prompts.</param>
/// <param name="TemplateRef">
/// Name of a prompt template registered in the host's <c>ISystemPromptComposer</c> /
/// <c>IPromptTemplate</c> DI keyspace. Combined with <see cref="Variables"/>.
/// </param>
/// <param name="FileRef">Path to a prompt file, relative to the manifest's directory.</param>
/// <param name="Variables">Optional variable bag for <see cref="TemplateRef"/>.</param>
public sealed record SystemPromptSpec(
    string? Inline = null,
    string? TemplateRef = null,
    string? FileRef = null,
    IReadOnlyDictionary<string, string>? Variables = null);
