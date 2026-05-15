// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// One named contribution to the LLM request that an <see cref="IContextProvider"/> emits.
/// Sections are the unit the section pipeline composes: the resolver orders them, the
/// section packer applies a token / character budget, and the flattener collapses them
/// into a <see cref="CompletionRequest"/> for the completion provider.
/// </summary>
/// <param name="Id">
/// Hierarchical, dot-separated identifier (e.g. <c>memory.user.long</c>, <c>retrieval.docs</c>,
/// <c>system.persona</c>). Validated by <see cref="SectionId.Validate"/>; allowed characters are
/// <c>[a-zA-Z0-9._-]</c>, no empty segments, no leading or trailing dot.
/// </param>
/// <param name="Kind">
/// Discriminator that drives how the flattener handles the payload. See <see cref="SectionKind"/>.
/// The <see cref="Payload"/>'s concrete type must match the <see cref="Kind"/>; the flattener
/// validates this when it consumes a section.
/// </param>
/// <param name="Payload">Typed content — see the <see cref="SectionPayload"/> hierarchy.</param>
/// <param name="Order">
/// Optional sort key within the same <see cref="Kind"/>. Null sections sort by registration
/// order (stable). Lower values come first, matching ASP.NET middleware ordering.
/// </param>
/// <param name="ProducerId">
/// Identifier of the component that produced the section. Conventionally the producer's type name.
/// Required for per-section observability (metrics, Langfuse metadata, structured logs); the
/// telemetry sinks use this as a label. Null is allowed for back-compat with the legacy
/// three-slot <see cref="ContextContribution"/> shim.
/// </param>
/// <param name="Budget">
/// Optional budget hint consumed by the section window packer. When the request exceeds budget,
/// the packer sheds sections in descending <see cref="SectionBudget.Priority"/> order
/// (higher number = drop first). Null sections are treated as priority 5 (mid).
/// </param>
public sealed record Section(
    string Id,
    SectionKind Kind,
    SectionPayload Payload,
    int? Order = null,
    string? ProducerId = null,
    SectionBudget? Budget = null)
{
    /// <inheritdoc cref="Section(string, SectionKind, SectionPayload, int?, string?, SectionBudget?)"/>
    public string Id { get; init; } = SectionId.Validate(Id);

    /// <inheritdoc cref="Section(string, SectionKind, SectionPayload, int?, string?, SectionBudget?)"/>
    public SectionPayload Payload { get; init; } = Payload ?? throw new ArgumentNullException(nameof(Payload));
}

/// <summary>
/// Discriminator on a <see cref="Section"/>. Drives how the flattener maps the section
/// into the final <see cref="CompletionRequest"/>.
/// </summary>
public enum SectionKind
{
    /// <summary>Text appended to <see cref="CompletionRequest.SystemPrompt"/>. Payload: <see cref="TextPayload"/>.</summary>
    SystemSegment,

    /// <summary>A user-role turn appended to <see cref="CompletionRequest.History"/>. Payload: <see cref="TurnPayload"/>.</summary>
    UserMessage,

    /// <summary>An assistant-role turn appended to <see cref="CompletionRequest.History"/>. Payload: <see cref="TurnPayload"/>.</summary>
    AssistantMessage,

    /// <summary>A tool-role turn appended to <see cref="CompletionRequest.History"/>. Payload: <see cref="TurnPayload"/>.</summary>
    ToolMessage,

    /// <summary>Tools added to <see cref="CompletionRequest.Tools"/>. Payload: <see cref="ToolsPayload"/>.</summary>
    ToolDeclaration,

    /// <summary>Structured-output spec set on <see cref="CompletionRequest.ResponseFormat"/>. Payload: <see cref="ResponseFormatPayload"/>. At most one section per turn.</summary>
    ResponseFormat,

    /// <summary>Observability-only — never flattens into the wire request. Payload: <see cref="MetadataPayload"/>.</summary>
    Metadata,
}

/// <summary>
/// Typed payload carried by a <see cref="Section"/>. The concrete type must match the
/// owning section's <see cref="SectionKind"/>; mismatches are rejected by the flattener.
/// </summary>
public abstract record SectionPayload;

/// <summary>Text content. Used by <see cref="SectionKind.SystemSegment"/>.</summary>
/// <param name="Value">Raw text; may be empty (will be skipped by the flattener).</param>
public sealed record TextPayload(string Value) : SectionPayload
{
    /// <inheritdoc cref="TextPayload(string)"/>
    public string Value { get; init; } = Value ?? throw new ArgumentNullException(nameof(Value));
}

/// <summary>A single chat turn. Used by <see cref="SectionKind.UserMessage"/>, <see cref="SectionKind.AssistantMessage"/>, and <see cref="SectionKind.ToolMessage"/>.</summary>
/// <param name="Turn">The turn to append to the conversation history.</param>
public sealed record TurnPayload(ChatTurn Turn) : SectionPayload
{
    /// <inheritdoc cref="TurnPayload(ChatTurn)"/>
    public ChatTurn Turn { get; init; } = Turn ?? throw new ArgumentNullException(nameof(Turn));
}

/// <summary>A bundle of tool declarations. Used by <see cref="SectionKind.ToolDeclaration"/>.</summary>
/// <param name="Tools">Tools to advertise to the model. Empty is allowed (no-op).</param>
public sealed record ToolsPayload(IReadOnlyList<ITool> Tools) : SectionPayload
{
    /// <inheritdoc cref="ToolsPayload(IReadOnlyList{ITool})"/>
    public IReadOnlyList<ITool> Tools { get; init; } = Tools ?? throw new ArgumentNullException(nameof(Tools));
}

/// <summary>A structured-output spec. Used by <see cref="SectionKind.ResponseFormat"/>.</summary>
/// <param name="Spec">Schema for the wire <c>response_format</c> hint.</param>
public sealed record ResponseFormatPayload(ResponseFormatSpec Spec) : SectionPayload
{
    /// <inheritdoc cref="ResponseFormatPayload(ResponseFormatSpec)"/>
    public ResponseFormatSpec Spec { get; init; } = Spec ?? throw new ArgumentNullException(nameof(Spec));
}

/// <summary>Observability-only key/value bag. Used by <see cref="SectionKind.Metadata"/>; never flattens to the wire.</summary>
/// <param name="Values">Arbitrary metadata. Keys should be stable; values must be serialisable for log shippers.</param>
public sealed record MetadataPayload(IReadOnlyDictionary<string, object?> Values) : SectionPayload
{
    /// <inheritdoc cref="MetadataPayload(IReadOnlyDictionary{string, object?})"/>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = Values ?? throw new ArgumentNullException(nameof(Values));
}

/// <summary>
/// Budget hint on a <see cref="Section"/>. Consumed by the section window packer when the
/// turn exceeds the configured request budget.
/// </summary>
/// <param name="Priority">
/// 0 (critical — never drop) through 10 (drop first). The packer sheds higher priority numbers
/// first when over budget. Mirrors ASP.NET middleware ordering semantics. Sections without a
/// <see cref="SectionBudget"/> are treated as priority 5.
/// </param>
/// <param name="MaxChars">
/// Optional per-section character cap. When set, the packer may truncate the payload to fit
/// without dropping the section entirely. Only honoured for <see cref="SectionKind.SystemSegment"/>
/// and <see cref="SectionKind.Metadata"/> kinds — turn-shaped sections are not truncated.
/// </param>
public sealed record SectionBudget(int Priority, int? MaxChars = null)
{
    /// <inheritdoc cref="SectionBudget(int, int?)"/>
    public int Priority { get; init; } = Priority is < 0 or > 10
        ? throw new ArgumentOutOfRangeException(nameof(Priority), Priority, "Priority must be between 0 (critical, never drop) and 10 (drop first).")
        : Priority;
}

/// <summary>
/// Validation helper for <see cref="Section.Id"/> values. Centralised because the same
/// rule set governs Langfuse metadata tag names (dots are normalised to underscores at the
/// Langfuse boundary) and Prometheus label values.
/// </summary>
public static class SectionId
{
    /// <summary>Maximum length of a section id, in characters.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Validate <paramref name="id"/> and return it on success.
    /// </summary>
    /// <param name="id">Candidate section id.</param>
    /// <returns><paramref name="id"/> unchanged when valid.</returns>
    /// <exception cref="ArgumentNullException">When <paramref name="id"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// When <paramref name="id"/> is empty, exceeds <see cref="MaxLength"/>, starts or ends with
    /// '.', contains an empty segment (e.g. <c>a..b</c>), or contains a character outside
    /// <c>[a-zA-Z0-9._-]</c>.
    /// </exception>
    public static string Validate(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (id.Length == 0)
        {
            throw new ArgumentException("Section id must not be empty.", nameof(id));
        }

        if (id.Length > MaxLength)
        {
            throw new ArgumentException($"Section id must be at most {MaxLength} characters (got {id.Length}).", nameof(id));
        }

        if (id[0] == '.' || id[^1] == '.')
        {
            throw new ArgumentException("Section id must not start or end with '.'.", nameof(id));
        }

        var previousWasDot = false;
        foreach (var ch in id)
        {
            if (ch == '.')
            {
                if (previousWasDot)
                {
                    throw new ArgumentException("Section id must not contain empty segments (e.g. 'a..b').", nameof(id));
                }

                previousWasDot = true;
                continue;
            }

            if (!IsAllowedChar(ch))
            {
                throw new ArgumentException(
                    $"Section id contains invalid character '{ch}'. Allowed: letters, digits, '.', '_', '-'.",
                    nameof(id));
            }

            previousWasDot = false;
        }

        return id;
    }

    private static bool IsAllowedChar(char ch)
        => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
}
