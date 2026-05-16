// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.Langfuse;

/// <summary>
/// Well-known <c>langfuse.*</c> activity tag names that the Langfuse UI reads. These
/// are additive aliases for the GenAI semantic-convention tags (ADR 0002), not a
/// replacement — both sets live on the same span.
/// </summary>
public static class LangfuseTags
{
    /// <summary>User identity for the trace.</summary>
    public const string UserId = "langfuse.user.id";

    /// <summary>Session identity for grouping related traces in the Langfuse UI.</summary>
    public const string SessionId = "langfuse.session.id";

    /// <summary>Human-readable trace name.</summary>
    public const string TraceName = "langfuse.trace.name";

    /// <summary>List of tags used for UI filtering.</summary>
    public const string Tags = "langfuse.tags";

    /// <summary>Prefix for structured metadata attributes; append the key (e.g. <c>langfuse.trace.metadata.flow_id</c>).</summary>
    public const string MetadataPrefix = "langfuse.trace.metadata.";

    /// <summary>
    /// Prefix for per-section context-window breakdown tags emitted by
    /// <c>LangfuseSectionEnrichment</c>. The full form is
    /// <c>langfuse.section.&lt;id_normalised&gt;.&lt;field&gt;</c> where <c>id_normalised</c>
    /// is the <see cref="Section.Id"/> with dots replaced by underscores (Langfuse renders
    /// dot-separated tag names as nested groups in the UI, so we normalise to one flat key).
    /// </summary>
    public const string SectionPrefix = "langfuse.section.";

    /// <summary>
    /// Well-known metadata key for the per-turn JSON section-breakdown blob, written as
    /// <c>langfuse.trace.metadata.section_breakdown</c>. Trace-review-friendly summary; the
    /// per-section <see cref="SectionPrefix"/> tags remain queryable in the UI filter bar.
    /// </summary>
    public const string SectionBreakdownMetadataKey = MetadataPrefix + "section_breakdown";
}
