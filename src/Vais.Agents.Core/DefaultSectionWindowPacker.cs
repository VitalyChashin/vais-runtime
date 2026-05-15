// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Default <see cref="ISectionWindowPacker"/>: identity when no budget is configured, otherwise
/// greedy shed by descending <see cref="SectionBudget.Priority"/> with size as a tiebreak.
/// Priority 0 sections are critical and never dropped; sections without an explicit
/// <see cref="SectionBudget"/> are treated as priority 5. Stateless.
/// </summary>
/// <remarks>
/// <para>
/// The packer measures size in tokens when <see cref="SectionBudgetContext.MaxTokens"/> is set
/// and an <see cref="ITokenCounter"/> is provided; otherwise it falls back to character
/// counting. <see cref="SectionKind.Metadata"/> sections never count toward the budget — they
/// don't reach the wire.
/// </para>
/// <para>
/// This packer does not currently truncate individual sections, even when
/// <see cref="SectionBudget.MaxChars"/> is set on a <see cref="SectionKind.SystemSegment"/> or
/// <see cref="SectionKind.Metadata"/> section. Per-section truncation is a documented future
/// extension; for SC-5 the shed strategy is whole-section drops only.
/// </para>
/// </remarks>
public sealed class DefaultSectionWindowPacker : ISectionWindowPacker
{
    /// <summary>Priority assigned to sections without an explicit <see cref="SectionBudget"/>.</summary>
    public const int DefaultPriority = 5;

    /// <summary>Shared singleton instance — the packer is stateless.</summary>
    public static DefaultSectionWindowPacker Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<SectionPackResult> PackAsync(
        IReadOnlyList<Section> sections,
        SectionBudgetContext budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(budget);

        var useTokens = budget.MaxTokens.HasValue && budget.TokenCounter is not null;
        var useChars = !useTokens && budget.MaxChars.HasValue;

        if (sections.Count == 0 || (!useTokens && !useChars))
        {
            return ValueTask.FromResult(BuildIdentityResult(sections));
        }

        var cap = useTokens ? budget.MaxTokens!.Value : budget.MaxChars!.Value;
        var sizes = MeasureAll(sections, useTokens ? budget.TokenCounter : null);
        var charSizes = useTokens ? MeasureAll(sections, counter: null) : sizes;
        var total = Sum(sizes);

        if (total <= cap)
        {
            return ValueTask.FromResult(BuildIdentityResult(sections));
        }

        // Drop loop: walk eligible sections in descending (priority, size) order until under cap
        // or no more drop candidates remain. Priority 0 is critical; never drop.
        var eligible = BuildEligibleDropOrder(sections, sizes);
        var dropped = new bool[sections.Count];

        foreach (var i in eligible)
        {
            if (total <= cap)
            {
                break;
            }

            dropped[i] = true;
            total -= sizes[i];
        }

        return ValueTask.FromResult(BuildShedResult(sections, dropped, charSizes));
    }

    private static SectionPackResult BuildIdentityResult(IReadOnlyList<Section> sections)
    {
        var outcomes = new PackerOutcome[sections.Count];
        for (var i = 0; i < sections.Count; i++)
        {
            outcomes[i] = new PackerOutcome(sections[i].Id, PackerOutcomes.Included);
        }

        return new SectionPackResult(sections, outcomes);
    }

    private static SectionPackResult BuildShedResult(
        IReadOnlyList<Section> sections,
        bool[] dropped,
        int[] charSizes)
    {
        var surviving = new List<Section>(sections.Count);
        var outcomes = new PackerOutcome[sections.Count];

        for (var i = 0; i < sections.Count; i++)
        {
            if (dropped[i])
            {
                outcomes[i] = new PackerOutcome(sections[i].Id, PackerOutcomes.Dropped, DroppedChars: charSizes[i]);
            }
            else
            {
                surviving.Add(sections[i]);
                outcomes[i] = new PackerOutcome(sections[i].Id, PackerOutcomes.Included);
            }
        }

        return new SectionPackResult(surviving, outcomes);
    }

    private static int[] MeasureAll(IReadOnlyList<Section> sections, ITokenCounter? counter)
    {
        var sizes = new int[sections.Count];
        for (var i = 0; i < sections.Count; i++)
        {
            sizes[i] = MeasureSection(sections[i], counter);
        }

        return sizes;
    }

    private static int MeasureSection(Section section, ITokenCounter? counter)
    {
        // Metadata never reaches the wire — exempt from budgeting.
        if (section.Kind == SectionKind.Metadata)
        {
            return 0;
        }

        var text = ExtractText(section);
        if (text.Length == 0)
        {
            return 0;
        }

        return counter?.Count(text) ?? text.Length;
    }

    private static string ExtractText(Section section) => section.Payload switch
    {
        TextPayload t => t.Value,
        TurnPayload t => t.Turn.Text,
        ToolsPayload t => SummariseTools(t.Tools),
        ResponseFormatPayload r => r.Spec.Schema.GetRawText(),
        _ => string.Empty,
    };

    private static string SummariseTools(IReadOnlyList<ITool> tools)
    {
        // Conservative size estimate for tools: name + description + parameter schema per tool.
        // The wire encoding (provider-specific) is more compact in some providers and less in
        // others; this is a stable, deterministic heuristic.
        if (tools.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var tool in tools)
        {
            sb.Append(tool.Name).Append(' ').Append(tool.Description).Append(' ').Append(tool.ParametersSchema.GetRawText()).Append(' ');
        }

        return sb.ToString();
    }

    private static List<int> BuildEligibleDropOrder(IReadOnlyList<Section> sections, int[] sizes)
    {
        var eligible = new List<int>(sections.Count);
        for (var i = 0; i < sections.Count; i++)
        {
            if (GetPriority(sections[i]) > 0)
            {
                eligible.Add(i);
            }
        }

        eligible.Sort((a, b) =>
        {
            var priCmp = GetPriority(sections[b]).CompareTo(GetPriority(sections[a]));
            if (priCmp != 0)
            {
                return priCmp;
            }

            var sizeCmp = sizes[b].CompareTo(sizes[a]);
            if (sizeCmp != 0)
            {
                return sizeCmp;
            }

            // Stable tiebreak — preserve registration order for equal (priority, size).
            return a.CompareTo(b);
        });

        return eligible;
    }

    private static int GetPriority(Section section)
        => section.Budget?.Priority ?? DefaultPriority;

    private static int Sum(int[] sizes)
    {
        var total = 0;
        foreach (var s in sizes)
        {
            total += s;
        }

        return total;
    }
}
