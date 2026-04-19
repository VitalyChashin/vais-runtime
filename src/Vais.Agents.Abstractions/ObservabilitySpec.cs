// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative observability overlays for an agent — Langfuse project, trace
/// sampling, custom tags. Overlays atop the OTel GenAI + <c>vais.*</c> semantic
/// conventions already shipped; consumed by the runtime at activation time.
/// </summary>
/// <param name="LangfuseProject">Langfuse project name. Null = use host-default project.</param>
/// <param name="SamplingRate">Per-agent trace sampling rate (0.0 – 1.0). Null = host-default.</param>
/// <param name="Tags">Static tags attached to every emitted trace / metric for this agent.</param>
/// <param name="TracingEnabled">Explicit on/off toggle. Null = host-default.</param>
public sealed record ObservabilitySpec(
    string? LangfuseProject = null,
    double? SamplingRate = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    bool? TracingEnabled = null);
