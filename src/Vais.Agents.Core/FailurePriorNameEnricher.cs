// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// Enrichment helper for <see cref="LlmAssistedRecipeInducer"/> when it decorates
/// a <see cref="FailurePatternInducer"/>. Deserializes <see cref="FailurePriorBody"/>
/// from the proposal body and calls the completion provider for a concise human-friendly
/// label. Returns <c>null</c> on bad JSON or empty response — the decorator's best-effort
/// discipline handles the passthrough.
/// </summary>
internal static class FailurePriorNameEnricher
{
    internal static async Task<string?> GenerateNameAsync(
        ICompletionProvider provider,
        RecipeProposal proposal,
        CancellationToken ct)
    {
        FailurePriorBody? body;
        try { body = JsonSerializer.Deserialize<FailurePriorBody>(proposal.Body); }
        catch { return null; }
        if (body is null) return null;

        var tool = body.ToolName is not null ? $"/{body.ToolName}" : string.Empty;
        var prompt =
            $"Give a concise (≤12 words) human-friendly label for this failure pattern: " +
            $"concept={body.ConceptName}, agent={body.AgentName}{tool}, occurrences={body.FailureCount}. " +
            $"Reply with just the label, no punctuation.";

        var request = new CompletionRequest(
            History: [new ChatTurn(AgentChatRole.User, prompt)]);
        var response = await provider.CompleteAsync(request, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(response.Text) ? null : response.Text.Trim();
    }
}
