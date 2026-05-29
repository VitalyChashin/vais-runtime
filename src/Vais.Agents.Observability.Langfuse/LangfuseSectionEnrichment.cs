// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Vais.Agents.Observability.Langfuse;

/// <summary>
/// <see cref="ISectionTelemetrySink"/> that decorates the current per-turn <see cref="Activity"/>
/// with <c>langfuse.section.*</c> tags + a single JSON-blob <c>section_breakdown</c> metadata
/// entry. Langfuse reads the activity through the standard OTel exporter; the metadata panel on
/// every generation surfaces both surfaces — filterable per-section tags and a human-readable
/// breakdown blob.
/// </summary>
/// <remarks>
/// <para>
/// Runs alongside the existing <c>LangfuseEnrichmentFilter</c> (don't merge — they fire at
/// different points: this on the section-telemetry event, the filter on the LLM call).
/// </para>
/// <para>
/// Section ids are normalised by replacing <c>.</c> with <c>_</c> for Langfuse UI compatibility
/// (dotted tag names are rendered as nested groups in the UI; flattened forms are easier to
/// filter on). For example <c>retrieval.docs</c> → <c>retrieval_docs</c>; resulting tag:
/// <c>langfuse.section.retrieval_docs.tokens = 412</c>.
/// </para>
/// <para>
/// Sink is a no-op when <see cref="Activity.Current"/> is null — opt-in via OTel pipeline.
/// </para>
/// </remarks>
public sealed class LangfuseSectionEnrichment : ISectionTelemetrySink
{
    /// <summary>Shared singleton — the sink is stateless.</summary>
    public static LangfuseSectionEnrichment Instance { get; } = new();

    /// <summary>Maximum characters written per section content tag. Longer content is truncated with an ellipsis.</summary>
    private const int ContentMaxChars = 2000;

    /// <inheritdoc />
    public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var activity = Activity.Current;
        if (activity is null)
        {
            return ValueTask.CompletedTask;
        }

        // Per-section flat tags: langfuse.section.<id_normalised>.{kind,producer,chars,ratio,tokens}.
        foreach (var section in snapshot.Sections)
        {
            var normalised = NormaliseId(section.Id);
            var prefix = LangfuseTags.SectionPrefix + normalised + ".";

            activity.SetTag(prefix + "kind", section.Kind.ToString());
            activity.SetTag(prefix + "chars", section.Chars);
            activity.SetTag(prefix + "ratio", section.Ratio.ToString("0.####", CultureInfo.InvariantCulture));

            if (section.ProducerId is not null)
            {
                activity.SetTag(prefix + "producer", section.ProducerId);
            }
            if (section.Tokens is int tokens)
            {
                activity.SetTag(prefix + "tokens", tokens);
            }
            if (section.Content is { Length: > 0 } content)
            {
                activity.SetTag(prefix + "content",
                    content.Length > ContentMaxChars
                        ? string.Concat(content.AsSpan(0, ContentMaxChars), "…")
                        : content);
            }
        }

        // One JSON blob for trace-review readability — operator can scan one field and see the
        // whole breakdown without expanding individual tags.
        activity.SetTag(LangfuseTags.SectionBreakdownMetadataKey, BuildBreakdownJson(snapshot));

        return ValueTask.CompletedTask;
    }

    private static string NormaliseId(string id)
    {
        if (id.IndexOf('.') < 0)
        {
            return id;
        }

        var sb = new StringBuilder(id.Length);
        foreach (var ch in id)
        {
            sb.Append(ch == '.' ? '_' : ch);
        }
        return sb.ToString();
    }

    private static string BuildBreakdownJson(SectionTelemetrySnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var section in snapshot.Sections)
            {
                // Use the ORIGINAL id (not the normalised form) inside the JSON — the JSON is
                // human-read; preserving dots is more readable. Only the OUTER tag name needs
                // normalisation for Langfuse's UI.
                writer.WriteNumber(section.Id, Math.Round(section.Ratio, 4));
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
