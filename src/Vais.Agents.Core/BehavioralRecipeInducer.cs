// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>Tuning knobs for <see cref="BehavioralRecipeInducer"/>.</summary>
public sealed record BehavioralRecipeInducerOptions
{
    /// <summary>Minimum number of distinct runs that exhibit a pattern before it gets proposed. Default 3.</summary>
    public int MinSupport { get; init; } = 3;

    /// <summary>Maximum sequence length to mine (2 = pairs only; 3 = also triples). Default 3.</summary>
    public int MaxSequenceLength { get; init; } = 3;

    /// <summary>Concepts containing any of these substrings (case-insensitive) flag the proposal as high-risk. Default destructive markers.</summary>
    public IReadOnlySet<string> HighRiskConceptSubstrings { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "delete", "destroy", "remove", "drop", "deploy", "apply" };
}

/// <summary>
/// Layer-2 (behavioral) induction over the trajectory corpus — mines same-run ordered
/// 2- and 3-gram concept sequences and emits each as a workflow <see cref="RecipeProposal"/>
/// with support count + confidence. Pure in-memory; runs on demand from a CLI / scheduled job.
/// </summary>
/// <remarks>
/// Algorithm (research §"Layer 2: behavioral / workflow ontology"):
/// <list type="number">
///   <item><description>Read events matching the supplied <see cref="TrajectoryQuery"/> from the store, group by <see cref="TrajectoryEvent.RunId"/>.</description></item>
///   <item><description>For each run, take its ordered concept sequence (skip events without <c>ConceptName</c>).</description></item>
///   <item><description>Tally ordered n-grams up to <c>MaxSequenceLength</c>; each pattern counts at most once per run.</description></item>
///   <item><description>For every pattern with support ≥ <c>MinSupport</c> across distinct runs, emit a proposal with confidence = support / totalRuns.</description></item>
/// </list>
/// Risk level: a pattern is high-risk if any concept in it matches the <c>HighRiskConceptSubstrings</c>
/// deny-list (default: <c>delete | destroy | remove | drop | deploy | apply</c>); medium for
/// pairs; low for length-3. This drives the Plan B approval gate reuse in Phase 4 — high-risk
/// always requires platform-level approval.
/// </remarks>
public sealed class BehavioralRecipeInducer(
    IInterceptorTeeStore store,
    BehavioralRecipeInducerOptions? options = null) : IRecipeInducer
{
    // ASCII Record Separator — won't collide with concept name chars; written as escape so
    // source is unambiguous regardless of editor encoding.
    private const char SequenceSeparator = '';

    private readonly IInterceptorTeeStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly BehavioralRecipeInducerOptions _options = options ?? new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecipeProposal>> InduceAsync(TrajectoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Pull the corpus; group by RunId so each run's concept sequence is one observation.
        var byRun = new Dictionary<string, List<(string Concept, string EventId)>>(StringComparer.Ordinal);
        await foreach (var evt in _store.QueryAsync(query, cancellationToken).ConfigureAwait(false))
        {
            if (evt.ConceptName is not { Length: > 0 } concept) continue;
            var run = evt.RunId ?? evt.EventId; // standalone events get their own "run"
            if (!byRun.TryGetValue(run, out var list))
            {
                list = new List<(string, string)>();
                byRun[run] = list;
            }
            list.Add((concept, evt.EventId));
        }

        if (byRun.Count == 0) return [];

        // Events arrive newest-first from the store; reverse per run so sequences read forward in time.
        foreach (var (_, list) in byRun) list.Reverse();

        var totalRuns = byRun.Count;
        var proposals = new List<RecipeProposal>();
        var now = DateTimeOffset.UtcNow;

        // Ordered n-gram mining, length 2..MaxSequenceLength. Same key counts at most once per run.
        var seqCounts = new Dictionary<string, (HashSet<string> Runs, List<string> TraceIds)>(StringComparer.Ordinal);
        foreach (var (runId, events) in byRun)
        {
            for (var n = 2; n <= _options.MaxSequenceLength; n++)
            {
                for (var i = 0; i + n <= events.Count; i++)
                {
                    var concepts = new string[n];
                    var ids = new string[n];
                    for (var k = 0; k < n; k++)
                    {
                        concepts[k] = events[i + k].Concept;
                        ids[k] = events[i + k].EventId;
                    }
                    var key = string.Join(SequenceSeparator, concepts);
                    if (!seqCounts.TryGetValue(key, out var agg))
                    {
                        agg = (new HashSet<string>(StringComparer.Ordinal), new List<string>());
                        seqCounts[key] = agg;
                    }
                    if (agg.Runs.Add(runId))
                    {
                        foreach (var id in ids) agg.TraceIds.Add(id);
                    }
                }
            }
        }

        foreach (var (key, agg) in seqCounts)
        {
            var support = agg.Runs.Count;
            if (support < _options.MinSupport) continue;
            var concepts = key.Split(SequenceSeparator);
            proposals.Add(new RecipeProposal
            {
                ProposalId = Guid.CreateVersion7().ToString("N"),
                Kind = RecipeProposalKind.WorkflowRecipe,
                Concept = concepts[^1], // anchor on the last concept in the sequence
                Body = string.Join(" -> ", concepts),
                Support = support,
                Confidence = (double)support / totalRuns,
                SourceTraceIds = agg.TraceIds,
                RiskLevel = ClassifyRiskForSequence(concepts),
                Status = RecipeProposalStatus.Pending,
                CreatedAt = now,
            });
        }

        // Sort: highest support first, then highest confidence.
        proposals.Sort((a, b) =>
        {
            var c = b.Support.CompareTo(a.Support);
            return c != 0 ? c : b.Confidence.CompareTo(a.Confidence);
        });
        return proposals;
    }

    private RecipeProposalRiskLevel ClassifyRiskForSequence(string[] concepts)
    {
        foreach (var c in concepts)
        {
            foreach (var s in _options.HighRiskConceptSubstrings)
                if (c.Contains(s, StringComparison.OrdinalIgnoreCase)) return RecipeProposalRiskLevel.High;
        }
        return concepts.Length <= 2 ? RecipeProposalRiskLevel.Medium : RecipeProposalRiskLevel.Low;
    }
}
