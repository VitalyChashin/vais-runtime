// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Plan D D-14 — decorates any <see cref="IRecipeProposalStore"/> with the
/// approve → overlay-write → catalog-reload pipeline. On a successful
/// <see cref="IRecipeProposalStore.DecideAsync"/> with <c>approve = true</c>,
/// merges the proposal into the overlay file via <see cref="IOntologyOverlayWriter"/>
/// (north overlay kinds) or <see cref="IFailureOntologyOverlayWriter"/> (Part 3
/// <see cref="RecipeProposalKind.FailurePrior"/>), then triggers
/// <see cref="IOntologyCatalogReloader.ReloadAsync"/> so the next <c>vais.describe</c>
/// reflects the change without a runtime restart.
/// </summary>
/// <remarks>
/// <para>
/// Side effects are best-effort and never roll back the proposal status — once a
/// human has approved, the decision is durable even if the overlay file is
/// temporarily unwritable. Failures are surfaced via the logger and as a thrown
/// exception only when the <c>throwOnSideEffectFailure</c> ctor flag is set; the
/// default is to log and swallow so the operator's `vais recipes approve` call
/// doesn't fail after the underlying decision already committed.
/// </para>
/// <para>
/// The reload step is skipped (with a single info log) if no
/// <see cref="IOntologyCatalogReloader"/> was supplied — useful when the host
/// hasn't wired the hot-reloadable catalog (the file is still updated, but the
/// in-process catalog stays stale until restart).
/// </para>
/// <para>
/// <see cref="RecipeProposalKind.FailurePrior"/> proposals are written to the
/// <see cref="FailureOntologyOverlay"/> file via <see cref="IFailureOntologyOverlayWriter"/>.
/// When either the writer or the failure overlay path is null, approved failure priors are
/// logged and skipped rather than written.
/// </para>
/// </remarks>
public sealed class OverlayPublishingRecipeProposalStoreDecorator : IRecipeProposalStore
{
    private readonly IRecipeProposalStore _inner;
    private readonly IOntologyOverlayWriter _writer;
    private readonly IFailureOntologyOverlayWriter? _failureWriter;
    private readonly IOntologyCatalogReloader? _reloader;
    private readonly string _overlayPath;
    private readonly string? _failureOverlayPath;
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private readonly bool _throwOnSideEffectFailure;

    /// <summary>Build the decorator.</summary>
    public OverlayPublishingRecipeProposalStoreDecorator(
        IRecipeProposalStore inner,
        IOntologyOverlayWriter writer,
        string overlayPath,
        IOntologyCatalogReloader? reloader = null,
        Microsoft.Extensions.Logging.ILogger? logger = null,
        bool throwOnSideEffectFailure = false,
        IFailureOntologyOverlayWriter? failureWriter = null,
        string? failureOverlayPath = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayPath);
        _overlayPath = overlayPath;
        _failureWriter = failureWriter;
        _failureOverlayPath = failureOverlayPath;
        _reloader = reloader;
        _logger = logger;
        _throwOnSideEffectFailure = throwOnSideEffectFailure;
    }

    /// <inheritdoc />
    public ValueTask UpsertAsync(RecipeProposal proposal, CancellationToken cancellationToken = default)
        => _inner.UpsertAsync(proposal, cancellationToken);

    /// <inheritdoc />
    public ValueTask<RecipeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
        => _inner.GetAsync(proposalId, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<RecipeProposal> ListAsync(RecipeProposalQuery query, CancellationToken cancellationToken = default)
        => _inner.ListAsync(query, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<RecipeProposal?> DecideAsync(string proposalId, bool approve, string decidedBy, CancellationToken cancellationToken = default)
    {
        var result = await _inner.DecideAsync(proposalId, approve, decidedBy, cancellationToken).ConfigureAwait(false);
        if (result is null || result.Status != RecipeProposalStatus.Approved) return result;

        try
        {
            bool changed;
            if (result.Kind == RecipeProposalKind.FailurePrior)
            {
                if (_failureWriter is null || string.IsNullOrEmpty(_failureOverlayPath))
                {
                    _logger?.LogInformation(
                        "FailurePrior proposal {ProposalId} approved but no IFailureOntologyOverlayWriter/failure overlay path is configured — skipping write.",
                        result.ProposalId);
                    return result;
                }
                changed = await _failureWriter.MergeAsync(result, _failureOverlayPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                changed = await _writer.MergeAsync(result, _overlayPath, cancellationToken).ConfigureAwait(false);
            }

            if (changed && _reloader is not null)
            {
                await _reloader.ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (changed)
            {
                _logger?.LogInformation(
                    "Recipe {ProposalId} merged into overlay {OverlayPath}; in-process catalog reload skipped (no IOntologyCatalogReloader registered).",
                    result.ProposalId, result.Kind == RecipeProposalKind.FailurePrior ? _failureOverlayPath : _overlayPath);
            }
        }
        catch (Exception ex)
        {
            var path = result.Kind == RecipeProposalKind.FailurePrior ? _failureOverlayPath : _overlayPath;
            _logger?.LogError(ex,
                "Failed to publish approved recipe {ProposalId} to overlay {OverlayPath}.",
                result.ProposalId, path);
            if (_throwOnSideEffectFailure) throw;
        }

        return result;
    }
}
