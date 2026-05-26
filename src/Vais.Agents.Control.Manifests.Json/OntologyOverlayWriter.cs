// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Writes an approved <see cref="RecipeProposal"/> back into the on-disk
/// <see cref="OntologyOverlay"/> JSON. Plan D D-13: closes the
/// descriptive↔normative loop — proposals approved through Plan B's gate land
/// in the same overlay file deployers edit by hand.
/// </summary>
/// <remarks>
/// <para>
/// Mapping: <see cref="RecipeProposalKind.WorkflowRecipe"/> appends a
/// <see cref="RecipeEntry"/> to <see cref="OntologyOverlay.Recipes"/>;
/// <see cref="RecipeProposalKind.TagSuggestion"/> adds tags to
/// <see cref="KindOverlay.Tags"/> for the proposal's <see cref="RecipeProposal.Concept"/>;
/// <see cref="RecipeProposalKind.DescriptionRewrite"/> sets
/// <see cref="KindOverlay.Description"/>.
/// </para>
/// <para>
/// Merge is idempotent: re-applying the same proposal does not duplicate tags,
/// does not append a second recipe with the same name, and is a no-op for
/// matching descriptions. Concurrent writes serialize on a per-path lock so two
/// approvals against the same overlay path cannot race a half-written file.
/// </para>
/// <para>
/// Write is atomic via temp-file-plus-rename: writes the merged JSON to
/// <c>&lt;path&gt;.tmp</c>, then <c>File.Move(temp, path, overwrite: true)</c>.
/// Output uses stable key ordering and 2-space indentation so the file stays
/// human-editable across rounds.
/// </para>
/// </remarks>
public interface IOntologyOverlayWriter
{
    /// <summary>
    /// Merge <paramref name="proposal"/> into the overlay at <paramref name="overlayPath"/>
    /// and write atomically. Returns <c>true</c> when the on-disk file changed,
    /// <c>false</c> when the proposal's content was already present.
    /// </summary>
    Task<bool> MergeAsync(RecipeProposal proposal, string overlayPath, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IOntologyOverlayWriter"/> implementation.</summary>
public sealed class JsonOntologyOverlayWriter : IOntologyOverlayWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<bool> MergeAsync(RecipeProposal proposal, string overlayPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayPath);

        var lockKey = Path.GetFullPath(overlayPath);
        var sem = Locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = OntologyOverlayLoader.LoadFromFile(overlayPath);
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
    /// Pure merge — exposed for tests and callers that want to drive serialization
    /// themselves. Returns the same <paramref name="existing"/> reference when the
    /// proposal is already present (no-op merge).
    /// </summary>
    public static OntologyOverlay Merge(OntologyOverlay existing, RecipeProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(proposal);

        return proposal.Kind switch
        {
            RecipeProposalKind.WorkflowRecipe => MergeWorkflowRecipe(existing, proposal),
            RecipeProposalKind.TagSuggestion => MergeTagSuggestion(existing, proposal),
            RecipeProposalKind.DescriptionRewrite => MergeDescriptionRewrite(existing, proposal),
            _ => existing,
        };
    }

    private static OntologyOverlay MergeWorkflowRecipe(OntologyOverlay existing, RecipeProposal proposal)
    {
        var recipes = existing.Recipes is { Count: > 0 } ? new List<RecipeEntry>(existing.Recipes) : new List<RecipeEntry>();
        var name = proposal.Name is { Length: > 0 } n ? n : RecipeNameForProposal(proposal);
        if (recipes.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal)))
            return existing; // already present

        var steps = proposal.Body.Split(" -> ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(concept => new RecipeStep { Kind = concept })
            .ToArray();
        recipes.Add(new RecipeEntry
        {
            Name = name,
            Description = $"Induced from {proposal.Support} run(s); confidence {proposal.Confidence:P0}; risk {proposal.RiskLevel}.",
            Steps = steps,
        });
        return existing with { Recipes = recipes };
    }

    private static OntologyOverlay MergeTagSuggestion(OntologyOverlay existing, RecipeProposal proposal)
    {
        var newTag = proposal.Body.Trim();
        if (newTag.Length == 0) return existing;

        var kinds = existing.Kinds is null
            ? new Dictionary<string, KindOverlay>(StringComparer.Ordinal)
            : new Dictionary<string, KindOverlay>(existing.Kinds, StringComparer.Ordinal);

        kinds.TryGetValue(proposal.Concept, out var ko);
        ko ??= new KindOverlay();
        var tags = ko.Tags is { Count: > 0 } ? new List<string>(ko.Tags) : new List<string>();
        if (tags.Contains(newTag, StringComparer.Ordinal)) return existing;
        tags.Add(newTag);
        kinds[proposal.Concept] = ko with { Tags = tags };
        return existing with { Kinds = kinds };
    }

    private static OntologyOverlay MergeDescriptionRewrite(OntologyOverlay existing, RecipeProposal proposal)
    {
        var newDescription = proposal.Body;
        var kinds = existing.Kinds is null
            ? new Dictionary<string, KindOverlay>(StringComparer.Ordinal)
            : new Dictionary<string, KindOverlay>(existing.Kinds, StringComparer.Ordinal);

        kinds.TryGetValue(proposal.Concept, out var ko);
        ko ??= new KindOverlay();
        if (string.Equals(ko.Description, newDescription, StringComparison.Ordinal)) return existing;
        kinds[proposal.Concept] = ko with { Description = newDescription };
        return existing with { Kinds = kinds };
    }

    private static string RecipeNameForProposal(RecipeProposal p)
    {
        // Stable + readable: concept slug + 8-char proposal id suffix.
        var slug = p.Concept.Replace(' ', '-').Trim().ToLowerInvariant();
        if (slug.Length == 0) slug = "recipe";
        var suffix = p.ProposalId.Length >= 8 ? p.ProposalId[..8] : p.ProposalId;
        return $"{slug}-{suffix}";
    }
}
