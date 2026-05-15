// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Bridges an existing <see cref="IContextWindowPacker"/> into the new
/// <see cref="ISectionWindowPacker"/> pipeline. The adapter flattens the input sections,
/// runs the legacy packer on the resulting <see cref="CompletionRequest"/>, and projects
/// the legacy packer's modifications back onto the section list on a best-effort basis.
/// </summary>
/// <remarks>
/// <para>Back-projection rules:</para>
/// <list type="bullet">
///   <item><description>Turn-kind sections (<see cref="SectionKind.UserMessage"/> / <see cref="SectionKind.AssistantMessage"/> / <see cref="SectionKind.ToolMessage"/>) survive when their <see cref="ChatTurn"/> appears in the packed request's <see cref="CompletionRequest.History"/> (value equality).</description></item>
///   <item><description>All <see cref="SectionKind.SystemSegment"/> sections survive when the packed request has a non-empty <see cref="CompletionRequest.SystemPrompt"/>; otherwise all are dropped. (Per-section truncation of system text is not back-projected.)</description></item>
///   <item><description>All <see cref="SectionKind.ToolDeclaration"/> sections survive when the packed request has non-empty <see cref="CompletionRequest.Tools"/>; otherwise all are dropped.</description></item>
///   <item><description><see cref="SectionKind.ResponseFormat"/> sections survive when the packed request has a non-null <see cref="CompletionRequest.ResponseFormat"/>.</description></item>
///   <item><description><see cref="SectionKind.Metadata"/> sections always survive.</description></item>
/// </list>
/// <para>
/// Outcomes are flagged with <see cref="PackerOutcomes.Legacy"/> for surviving sections and
/// <see cref="PackerOutcomes.Dropped"/> for dropped ones — the <c>"legacy"</c> tag signals
/// that the attribution is approximate. Users with non-trivial legacy packers should migrate
/// to <see cref="ISectionWindowPacker"/> directly to retain accurate per-section telemetry.
/// </para>
/// <para>
/// The <see cref="SectionBudgetContext"/> passed to <see cref="PackAsync"/> is ignored — the
/// legacy packer owns its own budget configuration and cannot consume the section-budget
/// shape.
/// </para>
/// </remarks>
public sealed class LegacyPackerAdapter : ISectionWindowPacker
{
    private readonly IContextWindowPacker _legacy;

    /// <summary>Wrap <paramref name="legacyPacker"/> for use in the section pipeline.</summary>
    /// <param name="legacyPacker">The legacy packer to bridge. Must not be null.</param>
    public LegacyPackerAdapter(IContextWindowPacker legacyPacker)
    {
        ArgumentNullException.ThrowIfNull(legacyPacker);
        _legacy = legacyPacker;
    }

    /// <inheritdoc />
    public async ValueTask<SectionPackResult> PackAsync(
        IReadOnlyList<Section> sections,
        SectionBudgetContext budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(budget);

        if (sections.Count == 0)
        {
            return new SectionPackResult(sections, Array.Empty<PackerOutcome>());
        }

        var tempReq = CompletionRequestFlattener.Flatten(sections);
        var packedReq = await _legacy.PackAsync(tempReq, cancellationToken).ConfigureAwait(false);

        // Identity short-circuit: the noop packer returns the same instance — the most common
        // path. Round-trip the input unchanged with "legacy" outcomes.
        if (ReferenceEquals(packedReq, tempReq))
        {
            return BuildAllLegacyResult(sections);
        }

        var survivingTurns = new HashSet<ChatTurn>(packedReq.History);
        var hasSystemPrompt = !string.IsNullOrEmpty(packedReq.SystemPrompt);
        var hasTools = packedReq.Tools is { Count: > 0 };
        var hasResponseFormat = packedReq.ResponseFormat is not null;

        var surviving = new List<Section>(sections.Count);
        var outcomes = new PackerOutcome[sections.Count];

        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var keep = ShouldKeep(section, survivingTurns, hasSystemPrompt, hasTools, hasResponseFormat);

            if (keep)
            {
                surviving.Add(section);
                outcomes[i] = new PackerOutcome(section.Id, PackerOutcomes.Legacy);
            }
            else
            {
                outcomes[i] = new PackerOutcome(section.Id, PackerOutcomes.Dropped, DroppedChars: SizeOf(section));
            }
        }

        return new SectionPackResult(surviving, outcomes);
    }

    private static SectionPackResult BuildAllLegacyResult(IReadOnlyList<Section> sections)
    {
        var outcomes = new PackerOutcome[sections.Count];
        for (var i = 0; i < sections.Count; i++)
        {
            outcomes[i] = new PackerOutcome(sections[i].Id, PackerOutcomes.Legacy);
        }

        return new SectionPackResult(sections, outcomes);
    }

    private static bool ShouldKeep(
        Section section,
        HashSet<ChatTurn> survivingTurns,
        bool hasSystemPrompt,
        bool hasTools,
        bool hasResponseFormat) => section.Kind switch
        {
            SectionKind.UserMessage or SectionKind.AssistantMessage or SectionKind.ToolMessage =>
                section.Payload is TurnPayload tp && survivingTurns.Contains(tp.Turn),
            SectionKind.SystemSegment => hasSystemPrompt,
            SectionKind.ToolDeclaration => hasTools,
            SectionKind.ResponseFormat => hasResponseFormat,
            SectionKind.Metadata => true,
            _ => true,
        };

    private static int SizeOf(Section section) => section.Payload switch
    {
        TextPayload t => t.Value.Length,
        TurnPayload t => t.Turn.Text.Length,
        ResponseFormatPayload r => r.Spec.Schema.GetRawText().Length,
        _ => 0,
    };
}
