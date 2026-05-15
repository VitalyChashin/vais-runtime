// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Applies a configured request budget to a resolved section list, dropping (or truncating)
/// sections that don't fit. Runs once per turn between <see cref="ISectionResolver"/> and the
/// section telemetry emitter. Stateless and reusable.
/// </summary>
/// <remarks>
/// The default shipped implementation (<c>DefaultSectionWindowPacker</c>) sheds sections by
/// descending <see cref="SectionBudget.Priority"/>, with size as a tiebreak. Sections without
/// an explicit <see cref="SectionBudget"/> are treated as priority 5 (the documented default).
/// Priority 0 is critical and never dropped. A <c>LegacyPackerAdapter</c> bridges existing
/// <see cref="IContextWindowPacker"/> implementations.
/// </remarks>
public interface ISectionWindowPacker
{
    /// <summary>
    /// Pack <paramref name="sections"/> to fit <paramref name="budget"/>.
    /// </summary>
    /// <param name="sections">Resolved sections, in canonical order (typically from <see cref="ISectionResolver"/>).</param>
    /// <param name="budget">Budget context — char cap, token cap, optional <see cref="ITokenCounter"/>. Use <see cref="SectionBudgetContext.Unlimited"/> to request identity behaviour.</param>
    /// <param name="cancellationToken">Cancellation token honoured by the implementation.</param>
    /// <returns>The surviving section list and the per-input-section outcomes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sections"/> or <paramref name="budget"/> is null.</exception>
    ValueTask<SectionPackResult> PackAsync(
        IReadOnlyList<Section> sections,
        SectionBudgetContext budget,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The packer's per-turn result: surviving sections (for the flattener) plus one
/// <see cref="PackerOutcome"/> per input section (for telemetry, in input order).
/// </summary>
/// <param name="Sections">The sections that survived budgeting. Subset of the input, in input order.</param>
/// <param name="Outcomes">One outcome per input section, in input order. <see cref="Outcomes"/>.Count equals the original input count, regardless of how many sections survived.</param>
public sealed record SectionPackResult(
    IReadOnlyList<Section> Sections,
    IReadOnlyList<PackerOutcome> Outcomes);

/// <summary>
/// Per-section attribution emitted by a packer. The telemetry emitter consumes this to record
/// which sections were kept, dropped, or truncated, and how many characters were shed.
/// </summary>
/// <param name="SectionId">The <see cref="Section.Id"/> being reported on.</param>
/// <param name="Outcome">One of the well-known values in <see cref="PackerOutcomes"/>.</param>
/// <param name="DroppedChars">
/// Number of characters dropped from this section, if any. Reported in characters regardless of
/// whether the budget was enforced in tokens or chars — chars are always measurable, tokens
/// require a counter that may not be available at attribution time.
/// </param>
public sealed record PackerOutcome(
    string SectionId,
    string Outcome,
    int DroppedChars = 0);

/// <summary>
/// Budget context passed to <see cref="ISectionWindowPacker.PackAsync"/>. Set <see cref="MaxChars"/>
/// for character-based budgeting; set both <see cref="MaxTokens"/> and <see cref="TokenCounter"/>
/// for token-based budgeting (tokens take precedence when both are configured).
/// </summary>
/// <param name="MaxChars">Optional character cap on the flattened request. Null ⇒ unlimited.</param>
/// <param name="MaxTokens">Optional token cap. Requires <see cref="TokenCounter"/>; otherwise ignored.</param>
/// <param name="TokenCounter">Tokenizer used when <see cref="MaxTokens"/> is set. Stateless.</param>
public sealed record SectionBudgetContext(
    int? MaxChars = null,
    int? MaxTokens = null,
    ITokenCounter? TokenCounter = null)
{
    /// <summary>An unlimited budget (no caps, no counter). Packers return identity on this input.</summary>
    public static SectionBudgetContext Unlimited { get; } = new();
}

/// <summary>Well-known values for <see cref="PackerOutcome.Outcome"/>.</summary>
public static class PackerOutcomes
{
    /// <summary>The section was kept verbatim.</summary>
    public const string Included = "included";

    /// <summary>The section's payload was shortened to fit a per-section budget. Only applicable to <see cref="SectionKind.SystemSegment"/> and <see cref="SectionKind.Metadata"/>.</summary>
    public const string Truncated = "truncated";

    /// <summary>The section was removed from the output.</summary>
    public const string Dropped = "dropped";

    /// <summary>The section was processed by <c>LegacyPackerAdapter</c>. Per-section attribution is approximate; the flag signals "treat as best-effort".</summary>
    public const string Legacy = "legacy";
}
