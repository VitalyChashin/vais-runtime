// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents;

/// <summary>
/// Collapses a resolved <see cref="Section"/> list into a stack-neutral
/// <see cref="CompletionRequest"/> that the completion provider can consume. Pure (modulo
/// optional warning logs) and stateless. Output shape is byte-equal to the pre-section
/// <see cref="CompletionRequest"/>: the wire layer (SK / MAF / OpenAI-compat) is unchanged.
/// </summary>
/// <remarks>
/// <para>The mapping is deterministic:</para>
/// <list type="bullet">
///   <item><description><see cref="SectionKind.SystemSegment"/> payloads concatenated by <c>"\n\n"</c> into <see cref="CompletionRequest.SystemPrompt"/>; empty <see cref="TextPayload.Value"/>s skipped.</description></item>
///   <item><description><see cref="SectionKind.UserMessage"/> / <see cref="SectionKind.AssistantMessage"/> / <see cref="SectionKind.ToolMessage"/> payloads collected in resolved order into <see cref="CompletionRequest.History"/>.</description></item>
///   <item><description><see cref="SectionKind.ToolDeclaration"/> payloads collected into <see cref="CompletionRequest.Tools"/>, deduplicated by <see cref="ITool.Name"/>; last occurrence wins. A warning is logged for each drop.</description></item>
///   <item><description><see cref="SectionKind.ResponseFormat"/> populates <see cref="CompletionRequest.ResponseFormat"/>. The resolver enforces a single such section; this flattener takes the first and warns if more are present (defence in depth).</description></item>
///   <item><description><see cref="SectionKind.Metadata"/> is ignored — it never reaches the model.</description></item>
///   <item><description><see cref="CompletionRequest.Temperature"/> and <see cref="CompletionRequest.MaxTokens"/> are copied from the optional <c>template</c> argument (if supplied). All other fields on the template are ignored — sections are authoritative for shape.</description></item>
/// </list>
/// </remarks>
public static class CompletionRequestFlattener
{
    /// <summary>
    /// Flatten <paramref name="sections"/> into a <see cref="CompletionRequest"/>.
    /// </summary>
    /// <param name="sections">Resolved sections, in canonical order (the output of <see cref="ISectionResolver"/>). Required.</param>
    /// <param name="template">Optional carrier for sampling hints (<see cref="CompletionRequest.Temperature"/>, <see cref="CompletionRequest.MaxTokens"/>). Only those two fields are read; everything else is derived from <paramref name="sections"/>.</param>
    /// <param name="logger">Optional logger for dedup warnings. <c>null</c> ⇒ silent.</param>
    /// <returns>A <see cref="CompletionRequest"/> ready for the completion provider.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sections"/> is null.</exception>
    public static CompletionRequest Flatten(
        IReadOnlyList<Section> sections,
        CompletionRequest? template = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        logger ??= NullLogger.Instance;

        return new CompletionRequest(
            History: BuildHistory(sections),
            SystemPrompt: BuildSystemPrompt(sections),
            Temperature: template?.Temperature,
            MaxTokens: template?.MaxTokens,
            Tools: BuildTools(sections, logger),
            ResponseFormat: BuildResponseFormat(sections, logger));
    }

    private static string? BuildSystemPrompt(IReadOnlyList<Section> sections)
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

    private static IReadOnlyList<ChatTurn> BuildHistory(IReadOnlyList<Section> sections)
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

        return (IReadOnlyList<ChatTurn>?)turns ?? Array.Empty<ChatTurn>();
    }

    private static IReadOnlyList<ITool>? BuildTools(IReadOnlyList<Section> sections, ILogger logger)
    {
        Dictionary<string, ITool>? byName = null;
        List<string>? order = null;

        foreach (var section in sections)
        {
            if (section.Kind != SectionKind.ToolDeclaration || section.Payload is not ToolsPayload payload)
            {
                continue;
            }

            foreach (var tool in payload.Tools)
            {
                byName ??= new Dictionary<string, ITool>(StringComparer.Ordinal);
                order ??= new List<string>();

                if (byName.ContainsKey(tool.Name))
                {
                    logger.LogWarning(
                        "Duplicate tool name '{ToolName}' encountered while flattening section '{SectionId}' (producer '{ProducerId}'); replacing the earlier occurrence.",
                        tool.Name,
                        section.Id,
                        section.ProducerId ?? "(null)");
                }
                else
                {
                    order.Add(tool.Name);
                }

                byName[tool.Name] = tool;
            }
        }

        if (byName is null || byName.Count == 0)
        {
            return null;
        }

        var result = new ITool[order!.Count];
        for (var i = 0; i < order.Count; i++)
        {
            result[i] = byName[order[i]];
        }

        return result;
    }

    private static ResponseFormatSpec? BuildResponseFormat(IReadOnlyList<Section> sections, ILogger logger)
    {
        ResponseFormatSpec? first = null;
        string? firstSectionId = null;

        foreach (var section in sections)
        {
            if (section.Kind != SectionKind.ResponseFormat || section.Payload is not ResponseFormatPayload payload)
            {
                continue;
            }

            if (first is null)
            {
                first = payload.Spec;
                firstSectionId = section.Id;
                continue;
            }

            logger.LogWarning(
                "Multiple ResponseFormat sections reached the flattener (first '{First}', also '{Other}'); keeping the first. The resolver normally rejects this — flattener defence in depth.",
                firstSectionId,
                section.Id);
        }

        return first;
    }
}
