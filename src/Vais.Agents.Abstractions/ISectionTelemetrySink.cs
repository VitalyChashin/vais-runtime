// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Receives a <see cref="SectionTelemetrySnapshot"/> for each turn the section pipeline produces.
/// Implementations fan out to a single observability backend (OpenTelemetry, Prometheus, Langfuse,
/// event bus, structured log, custom audit sink). The emitter invokes every registered sink in
/// registration order; sink failures are logged and swallowed so a buggy sink can't break the turn.
/// </summary>
/// <remarks>
/// Sinks must be cheap — they run on the turn's critical path. Costly work (HTTP exports, batching)
/// should happen asynchronously inside the sink, not in <see cref="EmitAsync"/>.
/// </remarks>
public interface ISectionTelemetrySink
{
    /// <summary>
    /// Emit a per-turn telemetry snapshot. Implementations must not mutate <paramref name="snapshot"/>.
    /// </summary>
    /// <param name="snapshot">Per-turn measurements and metadata. Never null.</param>
    /// <param name="cancellationToken">Cancellation token honoured by the sink.</param>
    ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>
/// One per-turn measurement record handed to every <see cref="ISectionTelemetrySink"/>. Carries
/// the run / agent / turn coordinates plus per-input-section measurements and the aggregate
/// budget summary.
/// </summary>
/// <param name="RunId">Stable run identifier stamped by <c>StatefulAiAgent</c>. Null only when no journal / context accessor is wired.</param>
/// <param name="AgentId">Stable agent identifier (typed name or <see cref="AgentContext.AgentName"/>). Null when not configured.</param>
/// <param name="TurnIndex">1-based turn index within the run (turn 1 is the first model call; tool-call loops increment).</param>
/// <param name="Sections">One measurement per section that entered the packer, in input order. Surviving and dropped sections both appear; the outcome field distinguishes them.</param>
/// <param name="Budget">Aggregate counters: budget target, used, dropped, truncated. Lets dashboards compute "% over budget" without re-summing the section list.</param>
public sealed record SectionTelemetrySnapshot(
    string? RunId,
    string? AgentId,
    int TurnIndex,
    IReadOnlyList<SectionMeasurement> Sections,
    SectionBudgetSummary Budget);

/// <summary>
/// Per-section measurement carried by a <see cref="SectionTelemetrySnapshot"/>. Cardinality is
/// bounded by the registered producer chain (typically 5–10 sections per turn).
/// </summary>
/// <param name="Id">The <see cref="Section.Id"/> being reported on.</param>
/// <param name="Kind">The <see cref="Section.Kind"/> discriminator.</param>
/// <param name="ProducerId">The <see cref="Section.ProducerId"/>, or null when not set.</param>
/// <param name="Order">The <see cref="Section.Order"/>, or null when the section uses registration-order positioning.</param>
/// <param name="Priority">The <see cref="SectionBudget.Priority"/>, or null when no explicit budget is set (treated as 5 by the packer).</param>
/// <param name="Chars">Character count of the section's textual payload. 0 for <see cref="SectionKind.Metadata"/> sections.</param>
/// <param name="Tokens">Token count if an <see cref="ITokenCounter"/> was wired into the packer; null otherwise.</param>
/// <param name="Ratio">Section share of the turn's total chars, 0–1. Computed against the sum of chars across all sections in the snapshot.</param>
/// <param name="Outcome">One of <see cref="PackerOutcomes.Included"/> / <see cref="PackerOutcomes.Truncated"/> / <see cref="PackerOutcomes.Dropped"/> / <see cref="PackerOutcomes.Legacy"/>.</param>
/// <param name="DroppedChars">Number of characters dropped from this section, if any. Mirrors <see cref="PackerOutcome.DroppedChars"/>.</param>
public sealed record SectionMeasurement(
    string Id,
    SectionKind Kind,
    string? ProducerId,
    int? Order,
    int? Priority,
    int Chars,
    int? Tokens,
    double Ratio,
    string Outcome,
    int DroppedChars);

/// <summary>Aggregate counters for the turn's section budget. Computed alongside <see cref="SectionMeasurement"/>s.</summary>
/// <param name="TargetChars">The <see cref="SectionBudgetContext.MaxChars"/>, or null when unlimited.</param>
/// <param name="TargetTokens">The <see cref="SectionBudgetContext.MaxTokens"/>, or null when unlimited.</param>
/// <param name="UsedChars">Sum of <see cref="SectionMeasurement.Chars"/> across surviving sections.</param>
/// <param name="UsedTokens">Sum of <see cref="SectionMeasurement.Tokens"/> across surviving sections, or null when no tokenizer was configured.</param>
/// <param name="UsedRatio">Used / target ratio. 0 when no budget is set. Computed as (used tokens / target tokens) when a tokenizer is present, else (used chars / target chars).</param>
/// <param name="DroppedCount">Number of sections dropped by the packer.</param>
/// <param name="TruncatedCount">Number of sections truncated by the packer.</param>
public sealed record SectionBudgetSummary(
    int? TargetChars,
    int? TargetTokens,
    int UsedChars,
    int? UsedTokens,
    double UsedRatio,
    int DroppedCount,
    int TruncatedCount);
