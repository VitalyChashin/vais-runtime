// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Writes an approved <see cref="RecipeProposalKind.FailurePrior"/> proposal back into the
/// on-disk <see cref="FailureOntologyOverlay"/> JSON (a <c>*.failure-ontology.json</c> file).
/// Mirrors <see cref="IOntologyOverlayWriter"/> but targets <see cref="FailureOntologyOverlay"/>
/// rather than <see cref="OntologyOverlay"/>.
/// </summary>
/// <remarks>
/// Merge is idempotent: re-applying the same prior (same concept + attribution path) replaces
/// the existing entry rather than appending a duplicate. Concurrent writes serialize on a
/// per-path lock. Write is atomic via temp-file-plus-rename.
/// </remarks>
public interface IFailureOntologyOverlayWriter
{
    /// <summary>
    /// Merge a <see cref="RecipeProposalKind.FailurePrior"/> <paramref name="proposal"/> into
    /// the <see cref="FailureOntologyOverlay"/> at <paramref name="overlayPath"/> and write
    /// atomically. Returns <c>true</c> when the on-disk file changed.
    /// </summary>
    Task<bool> MergeAsync(RecipeProposal proposal, string overlayPath, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IFailureOntologyOverlayWriter"/> implementation.</summary>
public sealed class JsonFailureOntologyOverlayWriter : IFailureOntologyOverlayWriter
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<bool> MergeAsync(
        RecipeProposal proposal,
        string overlayPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayPath);
        if (proposal.Kind != RecipeProposalKind.FailurePrior) return false;

        var lockKey = Path.GetFullPath(overlayPath);
        var sem = Locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = LoadOverlay(overlayPath);
            var merged = Merge(existing, proposal);
            if (ReferenceEquals(merged, existing)) return false;

            var json = JsonSerializer.Serialize(merged, WriteOptions);
            var dir = Path.GetDirectoryName(overlayPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = overlayPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, overlayPath, overwrite: true);
            return true;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Pure merge of a <see cref="RecipeProposalKind.FailurePrior"/> proposal into an existing
    /// <see cref="FailureOntologyOverlay"/>. Exposed for tests.
    /// </summary>
    public static FailureOntologyOverlay Merge(FailureOntologyOverlay existing, RecipeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Kind != RecipeProposalKind.FailurePrior) return existing;

        FailurePriorBody? priorBody;
        try
        {
            priorBody = JsonSerializer.Deserialize<FailurePriorBody>(proposal.Body);
        }
        catch
        {
            return existing;
        }

        if (priorBody is null || string.IsNullOrEmpty(priorBody.AttributionPath)) return existing;

        var attrs = existing.Attributions is null
            ? new Dictionary<string, FailureAttributionOverlay>(StringComparer.Ordinal)
            : new Dictionary<string, FailureAttributionOverlay>(existing.Attributions, StringComparer.Ordinal);

        attrs.TryGetValue(priorBody.AttributionPath, out var attrOverlay);
        attrOverlay ??= new FailureAttributionOverlay();

        var priors = attrOverlay.FailurePriors is { Count: > 0 }
            ? new List<FailurePriorBody>(attrOverlay.FailurePriors)
            : new List<FailurePriorBody>();

        // Replace existing prior with same concept, or append.
        var existingIdx = priors.FindIndex(p =>
            string.Equals(p.ConceptName, priorBody.ConceptName, StringComparison.Ordinal));

        if (existingIdx >= 0)
        {
            if (priors[existingIdx] == priorBody) return existing; // idempotent
            priors[existingIdx] = priorBody;
        }
        else
        {
            priors.Add(priorBody);
        }

        attrs[priorBody.AttributionPath] = attrOverlay with { FailurePriors = priors };
        return existing with { Attributions = attrs };
    }

    private FailureOntologyOverlay LoadOverlay(string path)
    {
        if (!File.Exists(path)) return FailureOntologyOverlay.Empty;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FailureOntologyOverlay>(json, ReadOptions)
                   ?? FailureOntologyOverlay.Empty;
        }
        catch
        {
            return FailureOntologyOverlay.Empty;
        }
    }
}
