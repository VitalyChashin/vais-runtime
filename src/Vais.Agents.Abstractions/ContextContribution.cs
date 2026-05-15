// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// What an <see cref="IContextProvider"/> contributes to a turn. The canonical surface is
/// <see cref="Sections"/> — a list of typed <see cref="Section"/> records the host resolves,
/// budgets, and flattens into the candidate <see cref="CompletionRequest"/> before it reaches
/// the model. The legacy three-slot view (<see cref="SystemPromptAddendum"/>,
/// <see cref="InjectedHistory"/>, <see cref="AdditionalTools"/>) is retained as an additive
/// shim so existing providers compile unchanged; the legacy constructor will be marked
/// <c>[Obsolete(DiagnosticId="VAIS0010")]</c> in SC-7 once all in-repo consumers migrate.
/// </summary>
/// <param name="SystemPromptAddendum">
/// Legacy view. Text appended to the candidate's system prompt with a <c>"\n\n"</c> separator.
/// When set via the legacy constructor, produces a single <see cref="SectionKind.SystemSegment"/>
/// section with id <c>system.legacy_addendum</c> and <c>ProducerId="legacy"</c>. When read on a
/// contribution built via the <see cref="ContextContribution(System.Collections.Generic.IReadOnlyList{Section})"/>
/// constructor, aggregates the text of every <see cref="SectionKind.SystemSegment"/> section.
/// </param>
/// <param name="InjectedHistory">
/// Legacy view. Extra turns to append after the candidate's history. When set via the legacy
/// constructor, produces one turn-shaped section per turn with ids <c>history.legacy_injected.0</c>,
/// <c>history.legacy_injected.1</c>, … and <c>ProducerId="legacy"</c>. When read on a contribution
/// built via the section-based constructor, projects every <see cref="SectionKind.UserMessage"/>,
/// <see cref="SectionKind.AssistantMessage"/>, and <see cref="SectionKind.ToolMessage"/> section
/// back into a flat list.
/// </param>
/// <param name="AdditionalTools">
/// Legacy view. Tools to append to the candidate's tool list. When set via the legacy
/// constructor, produces a single <see cref="SectionKind.ToolDeclaration"/> section with id
/// <c>tools.legacy_additional</c> and <c>ProducerId="legacy"</c>. When read on a contribution
/// built via the section-based constructor, flattens all <see cref="SectionKind.ToolDeclaration"/>
/// sections in registration order.
/// </param>
public sealed record ContextContribution(
    // TODO(SC-7): once all in-repo callers migrate to the section-based constructor, mark this
    // primary constructor with [Obsolete("Use ContextContribution(IReadOnlyList<Section>).",
    // DiagnosticId = "VAIS0010")]. Removal scheduled for v0.6.
    string? SystemPromptAddendum = null,
    IReadOnlyList<ChatTurn>? InjectedHistory = null,
    IReadOnlyList<ITool>? AdditionalTools = null)
{
    /// <summary>
    /// The sections this provider contributes — the canonical surface. Set directly by
    /// <see cref="ContextContribution(System.Collections.Generic.IReadOnlyList{Section})"/>;
    /// derived from the legacy three-slot parameters when the primary constructor is used.
    /// </summary>
    public IReadOnlyList<Section> Sections { get; init; }
        = BuildLegacySections(SystemPromptAddendum, InjectedHistory, AdditionalTools);

    /// <summary>
    /// Section-based constructor — the canonical entry point. The legacy three-slot view
    /// (<see cref="SystemPromptAddendum"/> / <see cref="InjectedHistory"/> / <see cref="AdditionalTools"/>)
    /// is derived from <paramref name="sections"/> so existing readers see a back-compatible projection.
    /// </summary>
    /// <param name="sections">Sections this provider contributes. Empty is allowed and equivalent to <see cref="Empty"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sections"/> is null.</exception>
    public ContextContribution(IReadOnlyList<Section> sections)
        : this(
            SystemPromptAddendum: DeriveSystemPromptAddendum(sections ?? throw new ArgumentNullException(nameof(sections))),
            InjectedHistory: DeriveInjectedHistory(sections),
            AdditionalTools: DeriveAdditionalTools(sections))
    {
        // Overwrite the auto-built legacy section list with the caller-supplied one so identity
        // and producer-attribution metadata is preserved end-to-end.
        Sections = sections;
    }

    /// <summary>A contribution that changes nothing. Useful sentinel for providers whose work is conditional.</summary>
    public static ContextContribution Empty { get; } = new();

    private const string LegacyProducerId = "legacy";
    private const string LegacySystemAddendumSectionId = "system.legacy_addendum";
    private const string LegacyInjectedHistorySectionIdPrefix = "history.legacy_injected.";
    private const string LegacyAdditionalToolsSectionId = "tools.legacy_additional";

    private static IReadOnlyList<Section> BuildLegacySections(
        string? addendum,
        IReadOnlyList<ChatTurn>? history,
        IReadOnlyList<ITool>? tools)
    {
        if (string.IsNullOrEmpty(addendum) && (history is null || history.Count == 0) && (tools is null || tools.Count == 0))
        {
            return Array.Empty<Section>();
        }

        var list = new List<Section>();

        if (!string.IsNullOrEmpty(addendum))
        {
            list.Add(new Section(
                LegacySystemAddendumSectionId,
                SectionKind.SystemSegment,
                new TextPayload(addendum),
                ProducerId: LegacyProducerId));
        }

        if (history is { Count: > 0 })
        {
            for (var i = 0; i < history.Count; i++)
            {
                list.Add(new Section(
                    LegacyInjectedHistorySectionIdPrefix + i,
                    MapTurnRoleToKind(history[i].Role),
                    new TurnPayload(history[i]),
                    Order: i,
                    ProducerId: LegacyProducerId));
            }
        }

        if (tools is { Count: > 0 })
        {
            list.Add(new Section(
                LegacyAdditionalToolsSectionId,
                SectionKind.ToolDeclaration,
                new ToolsPayload(tools),
                ProducerId: LegacyProducerId));
        }

        return list;
    }

    private static SectionKind MapTurnRoleToKind(AgentChatRole role) => role switch
    {
        AgentChatRole.User => SectionKind.UserMessage,
        AgentChatRole.Assistant => SectionKind.AssistantMessage,
        AgentChatRole.Tool => SectionKind.ToolMessage,
        AgentChatRole.System => SectionKind.SystemSegment,
        _ => SectionKind.UserMessage,
    };

    private static string? DeriveSystemPromptAddendum(IReadOnlyList<Section> sections)
    {
        List<string>? parts = null;
        foreach (var section in sections)
        {
            if (section.Kind != SectionKind.SystemSegment || section.Payload is not TextPayload text || text.Value.Length == 0)
            {
                continue;
            }

            parts ??= new List<string>();
            parts.Add(text.Value);
        }

        return parts is null ? null : string.Join("\n\n", parts);
    }

    private static IReadOnlyList<ChatTurn>? DeriveInjectedHistory(IReadOnlyList<Section> sections)
    {
        List<ChatTurn>? turns = null;
        foreach (var section in sections)
        {
            if (section.Payload is not TurnPayload turn)
            {
                continue;
            }

            if (section.Kind is not (SectionKind.UserMessage or SectionKind.AssistantMessage or SectionKind.ToolMessage))
            {
                continue;
            }

            turns ??= new List<ChatTurn>();
            turns.Add(turn.Turn);
        }

        return turns;
    }

    private static IReadOnlyList<ITool>? DeriveAdditionalTools(IReadOnlyList<Section> sections)
    {
        List<ITool>? tools = null;
        foreach (var section in sections)
        {
            if (section.Kind != SectionKind.ToolDeclaration || section.Payload is not ToolsPayload payload || payload.Tools.Count == 0)
            {
                continue;
            }

            tools ??= new List<ITool>();
            tools.AddRange(payload.Tools);
        }

        return tools;
    }
}
