// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;

namespace Vais.Agents.Observability.OpenTelemetry;

/// <summary>
/// Decorates the current per-turn <see cref="Activity"/> with per-section breakdown tags so
/// downstream OTel exporters (Jaeger, Tempo, Honeycomb, …) and Langfuse (which reads from the
/// same span) can attribute context-window load per producer. No-op when no <see cref="Activity"/>
/// is current — the section pipeline still runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tag layout</b> (cardinality bounded by the registered producer chain, typically 5–10 sections):
/// </para>
/// <list type="bullet">
///   <item><description><c>vais.request.turn_index</c>, <c>vais.request.section_count</c>, <c>vais.request.total_chars</c>, <c>vais.request.budget_used_ratio</c> — always emitted.</description></item>
///   <item><description><c>vais.request.total_tokens_est</c> — only when a token counter was configured on the budget.</description></item>
///   <item><description><c>vais.request.budget.target_chars</c> / <c>...target_tokens</c> — only when the corresponding budget cap is set.</description></item>
///   <item><description><c>vais.request.budget.dropped_count</c> / <c>...truncated_count</c> — always emitted (zero when no shedding).</description></item>
///   <item><description>Per section: <c>vais.request.section.&lt;id&gt;.{kind,chars,ratio,outcome}</c> always; <c>{producer,order,tokens,dropped_chars}</c> conditionally on presence.</description></item>
/// </list>
/// <para>
/// <b>Naming note.</b> Section ids may contain dots (e.g. <c>memory.user.long</c>), which produce
/// dotted tag names like <c>vais.request.section.memory.user.long.kind</c>. OTel allows this;
/// Langfuse's tag-name normaliser swaps dots for underscores at the boundary (see SC-12).
/// </para>
/// </remarks>
public sealed class OtelSectionSink : ISectionTelemetrySink
{
    /// <summary>Shared singleton — the sink is stateless.</summary>
    public static OtelSectionSink Instance { get; } = new();

    // Aggregate tag names.
    private const string TurnIndexTag = "vais.request.turn_index";
    private const string SectionCountTag = "vais.request.section_count";
    private const string TotalCharsTag = "vais.request.total_chars";
    private const string TotalTokensEstTag = "vais.request.total_tokens_est";
    private const string BudgetTargetCharsTag = "vais.request.budget.target_chars";
    private const string BudgetTargetTokensTag = "vais.request.budget.target_tokens";
    private const string BudgetUsedRatioTag = "vais.request.budget_used_ratio";
    private const string BudgetDroppedCountTag = "vais.request.budget.dropped_count";
    private const string BudgetTruncatedCountTag = "vais.request.budget.truncated_count";
    private const string SectionTagPrefix = "vais.request.section.";

    /// <inheritdoc />
    public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var activity = Activity.Current;
        if (activity is null)
        {
            // No span to decorate — telemetry is opt-in via tracer pipeline. Not an error.
            return ValueTask.CompletedTask;
        }

        // Aggregate tags.
        activity.SetTag(TurnIndexTag, snapshot.TurnIndex);
        activity.SetTag(SectionCountTag, snapshot.Sections.Count);
        activity.SetTag(TotalCharsTag, snapshot.Budget.UsedChars);
        activity.SetTag(BudgetUsedRatioTag, snapshot.Budget.UsedRatio);
        activity.SetTag(BudgetDroppedCountTag, snapshot.Budget.DroppedCount);
        activity.SetTag(BudgetTruncatedCountTag, snapshot.Budget.TruncatedCount);

        if (snapshot.Budget.UsedTokens is int usedTokens)
        {
            activity.SetTag(TotalTokensEstTag, usedTokens);
        }
        if (snapshot.Budget.TargetChars is int targetChars)
        {
            activity.SetTag(BudgetTargetCharsTag, targetChars);
        }
        if (snapshot.Budget.TargetTokens is int targetTokens)
        {
            activity.SetTag(BudgetTargetTokensTag, targetTokens);
        }

        // Per-section tags.
        foreach (var section in snapshot.Sections)
        {
            var prefix = SectionTagPrefix + section.Id + ".";
            activity.SetTag(prefix + "kind", section.Kind.ToString());
            activity.SetTag(prefix + "chars", section.Chars);
            activity.SetTag(prefix + "ratio", section.Ratio.ToString("0.####", CultureInfo.InvariantCulture));
            activity.SetTag(prefix + "outcome", section.Outcome);

            if (section.ProducerId is not null)
            {
                activity.SetTag(prefix + "producer", section.ProducerId);
            }
            if (section.Order is int order)
            {
                activity.SetTag(prefix + "order", order);
            }
            if (section.Tokens is int tokens)
            {
                activity.SetTag(prefix + "tokens", tokens);
            }
            if (section.DroppedChars > 0)
            {
                activity.SetTag(prefix + "dropped_chars", section.DroppedChars);
            }
        }

        return ValueTask.CompletedTask;
    }
}
