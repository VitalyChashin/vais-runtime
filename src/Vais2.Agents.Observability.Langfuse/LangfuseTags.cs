// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Observability.Langfuse;

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
}
