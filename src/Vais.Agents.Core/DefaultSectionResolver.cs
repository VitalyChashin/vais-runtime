// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Default <see cref="ISectionResolver"/>: detects id collisions and the ResponseFormat-singleton
/// violation, then sorts by (kind rank, effective order, registration index). Stateless and
/// safe to share across turns.
/// </summary>
public sealed class DefaultSectionResolver : ISectionResolver
{
    /// <summary>A reusable shared instance — the resolver is stateless.</summary>
    public static DefaultSectionResolver Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Section>> ResolveAsync(
        IReadOnlyList<Section> contributed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contributed);

        if (contributed.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<Section>>(Array.Empty<Section>());
        }

        DetectIdCollisions(contributed);
        DetectMultipleResponseFormat(contributed);

        return ValueTask.FromResult(Sort(contributed));
    }

    private static void DetectIdCollisions(IReadOnlyList<Section> sections)
    {
        var seen = new Dictionary<string, Section>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (seen.TryGetValue(section.Id, out var existing))
            {
                throw new SectionCollisionException(
                    section.Id,
                    new[] { existing.ProducerId, section.ProducerId },
                    $"Two sections share id '{section.Id}' " +
                    $"(producers: '{existing.ProducerId ?? "(null)"}' and '{section.ProducerId ?? "(null)"}'). " +
                    "Producers must namespace their section ids to avoid collisions.");
            }

            seen[section.Id] = section;
        }
    }

    private static void DetectMultipleResponseFormat(IReadOnlyList<Section> sections)
    {
        Section? first = null;
        foreach (var section in sections)
        {
            if (section.Kind != SectionKind.ResponseFormat)
            {
                continue;
            }

            if (first is null)
            {
                first = section;
                continue;
            }

            throw new SectionCollisionException(
                nameof(SectionKind.ResponseFormat),
                new[] { first.ProducerId, section.ProducerId },
                $"At most one ResponseFormat section is allowed per turn; found '{first.Id}' " +
                $"(producer '{first.ProducerId ?? "(null)"}') and '{section.Id}' " +
                $"(producer '{section.ProducerId ?? "(null)"}'). A single 'format.response' section is canonical.");
        }
    }

    private static IReadOnlyList<Section> Sort(IReadOnlyList<Section> sections)
    {
        // Decorate-sort-undecorate. Effective order: section.Order ?? registrationIndex
        // (null falls back to position in input). Stable tiebreak by registrationIndex.
        var indexed = new (Section Section, int Index, int Effective)[sections.Count];
        for (var i = 0; i < sections.Count; i++)
        {
            indexed[i] = (sections[i], i, sections[i].Order ?? i);
        }

        Array.Sort(indexed, static (a, b) =>
        {
            var rankCmp = KindRank(a.Section.Kind).CompareTo(KindRank(b.Section.Kind));
            if (rankCmp != 0)
            {
                return rankCmp;
            }

            var ordCmp = a.Effective.CompareTo(b.Effective);
            if (ordCmp != 0)
            {
                return ordCmp;
            }

            return a.Index.CompareTo(b.Index);
        });

        var result = new Section[indexed.Length];
        for (var i = 0; i < indexed.Length; i++)
        {
            result[i] = indexed[i].Section;
        }

        return result;
    }

    private static int KindRank(SectionKind kind) => kind switch
    {
        SectionKind.SystemSegment => 0,
        SectionKind.UserMessage or SectionKind.AssistantMessage or SectionKind.ToolMessage => 1,
        SectionKind.ToolDeclaration => 2,
        SectionKind.ResponseFormat => 3,
        SectionKind.Metadata => 4,
        _ => 99,
    };
}
