// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Decorator over an <see cref="IRecipeInducer"/> that lets the host enrich each proposal's
/// <see cref="RecipeProposal.Name"/> via an injected delegate — typically backed by
/// <c>ILlmGateway</c>. The decorator never instantiates a provider SDK directly (P4); the
/// delegate is the only outbound surface and is fully replaceable in tests.
/// </summary>
/// <remarks>
/// <para>
/// Plan D §"Induction proposes; humans dispose": this stage only assigns a friendly name; it
/// does not change <see cref="RecipeProposal.Body"/>, <see cref="RecipeProposal.Support"/>,
/// <see cref="RecipeProposal.Confidence"/>, or any approval-bearing field. The Phase-4
/// approval gate stays the sole authority on whether a proposal lands in the overlay.
/// </para>
/// <para>
/// If the enricher throws or returns <c>null</c>/empty, the original proposal is passed
/// through unchanged. This keeps the LLM path strictly best-effort — induction must still
/// produce usable proposals if the gateway is unhealthy or budget-throttled.
/// </para>
/// </remarks>
public sealed class LlmAssistedRecipeInducer(
    IRecipeInducer inner,
    Func<RecipeProposal, CancellationToken, Task<string?>> enricher) : IRecipeInducer
{
    private readonly IRecipeInducer _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<RecipeProposal, CancellationToken, Task<string?>> _enricher =
        enricher ?? throw new ArgumentNullException(nameof(enricher));

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipeProposal>> InduceAsync(TrajectoryQuery query, CancellationToken cancellationToken = default)
    {
        var raw = await _inner.InduceAsync(query, cancellationToken).ConfigureAwait(false);
        if (raw.Count == 0) return raw;

        var enriched = new List<RecipeProposal>(raw.Count);
        foreach (var proposal in raw)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? name = null;
            try
            {
                name = await _enricher(proposal, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best-effort: keep the original proposal if the enricher fails.
                name = null;
            }

            enriched.Add(string.IsNullOrWhiteSpace(name) ? proposal : proposal with { Name = name.Trim() });
        }
        return enriched;
    }
}
