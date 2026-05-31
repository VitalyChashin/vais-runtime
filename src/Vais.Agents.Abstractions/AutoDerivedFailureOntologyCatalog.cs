// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Stateless, pure-code implementation of <see cref="IFailureOntologyCatalog"/> whose base
/// taxonomy is auto-derived from <see cref="RunHealthSignalKind"/>. No I/O, no DI surface
/// beyond registering the singleton. Registrations that supply a
/// <see cref="FailureOntologyOverlay"/> should use <c>OverlaidFailureOntologyCatalog</c> instead.
/// </summary>
public sealed class AutoDerivedFailureOntologyCatalog : IFailureOntologyCatalog
{
    /// <summary>Shared singleton — safe for DI registration and direct use in tests.</summary>
    public static readonly AutoDerivedFailureOntologyCatalog Instance = new();

    private static readonly FailureConcept[] BaseConcepts =
    [
        // ── Mechanical — sourced from the agent event bus ──────────────────────
        new("ToolError", FailureAxis.Mechanical, FailureLevel.Warning,
            "A tool invocation failed; typically fed back to the model and recovered.",
            [RunHealthSignalKind.ToolError]),

        new("McpToolError", FailureAxis.Mechanical, FailureLevel.Warning,
            "An MCP gateway tool call failed.",
            [RunHealthSignalKind.McpError]),

        new("LlmCallRetried", FailureAxis.Mechanical, FailureLevel.Warning,
            "An outgoing LLM call was retried after a transient failure.",
            [RunHealthSignalKind.LlmRetry]),

        new("LlmFallbackEngaged", FailureAxis.Mechanical, FailureLevel.Warning,
            "An LLM call fell back from the primary provider to a secondary.",
            [RunHealthSignalKind.LlmFallback]),

        new("LlmCallFailure", FailureAxis.Mechanical, FailureLevel.Error,
            "An LLM call failed at the gateway without recovery.",
            [RunHealthSignalKind.LlmError]),

        new("TurnFailed", FailureAxis.Mechanical, FailureLevel.Error,
            "An agent turn failed with an exception before completion.",
            [RunHealthSignalKind.TurnFailed]),

        new("PluginPartial", FailureAxis.Mechanical, FailureLevel.Warning,
            "A turn completed but delivered a degraded or partial result.",
            [RunHealthSignalKind.TurnPartial]),

        new("GuardrailTriggered", FailureAxis.Mechanical, FailureLevel.Warning,
            "A guardrail (input, tool, or output) blocked or modified an operation.",
            [RunHealthSignalKind.Guardrail]),

        new("GraphNodeFailed", FailureAxis.Mechanical, FailureLevel.Error,
            "A graph node failed during execution.",
            [RunHealthSignalKind.NodeFailed]),

        // ── Quality seeds — populated by the eval mechanical axis (not from RunHealthSignalKind) ──
        new("JudgeMiss", FailureAxis.Quality, FailureLevel.Error,
            "A judge-score assertion missed the quality threshold.",
            []),

        new("AssertionFail", FailureAxis.Quality, FailureLevel.Error,
            "An eval assertion failed.",
            []),
    ];

    private static readonly IReadOnlyDictionary<string, FailureConcept> ByName =
        BaseConcepts.ToDictionary(c => c.Name, StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<RunHealthSignalKind, FailureConcept> ByKind =
        BaseConcepts
            .Where(c => c.SourceKinds.Count > 0)
            .SelectMany(c => c.SourceKinds.Select(k => (k, c)))
            .ToDictionary(t => t.k, t => t.c);

    /// <inheritdoc/>
    public string OntologyVersion => "auto-derived-1.0";

    /// <inheritdoc/>
    public IReadOnlyCollection<FailureConcept> Concepts => BaseConcepts;

    /// <inheritdoc/>
    public FailureConcept? Get(string conceptName) =>
        ByName.TryGetValue(conceptName, out var c) ? c : null;

    /// <inheritdoc/>
    public FailureConcept? FromSignalKind(RunHealthSignalKind kind) =>
        ByKind.TryGetValue(kind, out var c) ? c : null;

    /// <inheritdoc/>
    public bool IsMatchOrDescendant(string candidateName, string filterName)
    {
        if (string.Equals(candidateName, filterName, StringComparison.Ordinal))
            return true;
        var current = Get(candidateName);
        while (current?.ParentName is not null)
        {
            if (string.Equals(current.ParentName, filterName, StringComparison.Ordinal))
                return true;
            current = Get(current.ParentName);
        }
        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<(string AttributionPath, FailurePriorBody Prior)> GetPriorsForConcept(
        string conceptName) => [];
}
