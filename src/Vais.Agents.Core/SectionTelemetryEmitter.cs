// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents;

/// <summary>
/// Computes a <see cref="SectionTelemetrySnapshot"/> from the section pipeline's per-turn state
/// and fans it out to a registered list of <see cref="ISectionTelemetrySink"/>s. Runs once per
/// turn between the section window packer and the flattener.
/// </summary>
/// <remarks>
/// <para>
/// The emitter is the single integration point for observability — adding a new backend means
/// adding a sink to the list, not threading data through every layer of the pipeline.
/// </para>
/// <para>
/// Sink failures are logged at <c>Warning</c> and swallowed: the turn continues even if a sink
/// throws. Telemetry must not break the data path.
/// </para>
/// </remarks>
public sealed class SectionTelemetryEmitter
{
    private readonly IReadOnlyList<ISectionTelemetrySink> _sinks;
    private readonly ILogger _logger;

    /// <summary>An emitter with no sinks — a no-op convenient as a default when telemetry isn't wired.</summary>
    public static SectionTelemetryEmitter NoOp { get; } = new(Array.Empty<ISectionTelemetrySink>(), NullLogger.Instance);

    /// <summary>Create an emitter that fans out to <paramref name="sinks"/>.</summary>
    /// <param name="sinks">The sinks to invoke per turn. Null or empty produces a no-op emitter (zero allocations on the hot path).</param>
    /// <param name="logger">Logger for sink-failure warnings. Null falls back to <see cref="NullLogger.Instance"/>.</param>
    public SectionTelemetryEmitter(IEnumerable<ISectionTelemetrySink>? sinks, ILogger? logger = null)
    {
        _sinks = sinks?.ToArray() ?? Array.Empty<ISectionTelemetrySink>();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>True when no sinks are configured. Hot-path callers can skip snapshot construction entirely.</summary>
    public bool IsNoOp => _sinks.Count == 0;

    /// <summary>
    /// Build a <see cref="SectionTelemetrySnapshot"/> from the per-turn pipeline state and dispatch
    /// it to every sink in registration order.
    /// </summary>
    /// <param name="inputSections">Sections handed to the packer — the resolver's output. Always non-null; empty means no sections this turn.</param>
    /// <param name="packResult">The packer's result: surviving sections + per-input outcomes (one entry per <paramref name="inputSections"/>, in input order).</param>
    /// <param name="budget">The budget context the packer ran against.</param>
    /// <param name="context">Ambient <see cref="AgentContext"/> at emission time. Sinks read <c>RunId</c>, <c>AgentName</c>, and other ambient fields from this. Pass <see cref="AgentContext.Empty"/> when no context is available.</param>
    /// <param name="turnIndex">1-based turn index within the run.</param>
    /// <param name="cancellationToken">Cancellation token honoured by the sinks.</param>
    public async ValueTask EmitAsync(
        IReadOnlyList<Section> inputSections,
        SectionPackResult packResult,
        SectionBudgetContext budget,
        AgentContext context,
        int turnIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputSections);
        ArgumentNullException.ThrowIfNull(packResult);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(context);

        if (_sinks.Count == 0)
        {
            return;
        }

        var snapshot = BuildSnapshot(inputSections, packResult, budget, context, turnIndex);

        foreach (var sink in _sinks)
        {
            try
            {
                await sink.EmitAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Section telemetry sink {SinkType} threw on turn {TurnIndex}; swallowed.",
                    sink.GetType().Name,
                    turnIndex);
            }
        }
    }

    private static SectionTelemetrySnapshot BuildSnapshot(
        IReadOnlyList<Section> inputSections,
        SectionPackResult packResult,
        SectionBudgetContext budget,
        AgentContext context,
        int turnIndex)
    {
        // Pre-compute char + optional token sizes for every input section, plus the totals
        // (chars only — surviving). One pass per metric keeps the code readable and the cost
        // is tiny relative to an LLM call.
        var charSizes = new int[inputSections.Count];
        int?[]? tokenSizes = budget.TokenCounter is null ? null : new int?[inputSections.Count];

        var totalChars = 0;
        int? totalTokens = budget.TokenCounter is null ? null : 0;
        for (var i = 0; i < inputSections.Count; i++)
        {
            var section = inputSections[i];
            var text = ExtractText(section);
            var chars = section.Kind == SectionKind.Metadata ? 0 : text.Length;
            charSizes[i] = chars;
            totalChars += chars;

            if (tokenSizes is not null)
            {
                var tok = section.Kind == SectionKind.Metadata || text.Length == 0
                    ? 0
                    : budget.TokenCounter!.Count(text);
                tokenSizes[i] = tok;
                totalTokens += tok;
            }
        }

        // Build per-section measurements. Outcomes from the packer are in input order, so we can
        // zip them with inputSections by index. Ratio is computed against the total chars (the
        // universal measurable; tokens would require the same denominator and are reported
        // separately when available).
        var droppedCount = 0;
        var truncatedCount = 0;
        var usedChars = 0;
        int? usedTokens = tokenSizes is null ? null : 0;
        var measurements = new SectionMeasurement[inputSections.Count];

        for (var i = 0; i < inputSections.Count; i++)
        {
            var section = inputSections[i];
            var outcome = i < packResult.Outcomes.Count ? packResult.Outcomes[i] : null;
            var outcomeLabel = outcome?.Outcome ?? PackerOutcomes.Included;
            var droppedChars = outcome?.DroppedChars ?? 0;

            var chars = charSizes[i];
            var tokens = tokenSizes?[i];
            var ratio = totalChars == 0 ? 0d : (double)chars / totalChars;

            var text = ExtractText(section);
            measurements[i] = new SectionMeasurement(
                Id: section.Id,
                Kind: section.Kind,
                ProducerId: section.ProducerId,
                Order: section.Order,
                Priority: section.Budget?.Priority,
                Chars: chars,
                Tokens: tokens,
                Ratio: ratio,
                Outcome: outcomeLabel,
                DroppedChars: droppedChars)
            {
                Content = section.Kind != SectionKind.Metadata && text.Length > 0 ? text : null,
            };

            switch (outcomeLabel)
            {
                case PackerOutcomes.Dropped:
                    droppedCount++;
                    break;
                case PackerOutcomes.Truncated:
                    truncatedCount++;
                    usedChars += chars;
                    if (tokens is not null) usedTokens += tokens;
                    break;
                default:
                    usedChars += chars;
                    if (tokens is not null) usedTokens += tokens;
                    break;
            }
        }

        // Budget-used ratio prefers token measurement when available; falls back to chars; falls
        // back to 0 when no budget is configured (the typical no-sinks default path is already
        // short-circuited, so this only fires when sinks are wired with an unlimited budget).
        var usedRatio = ComputeUsedRatio(budget, usedChars, usedTokens);

        var summary = new SectionBudgetSummary(
            TargetChars: budget.MaxChars,
            TargetTokens: budget.MaxTokens,
            UsedChars: usedChars,
            UsedTokens: usedTokens,
            UsedRatio: usedRatio,
            DroppedCount: droppedCount,
            TruncatedCount: truncatedCount);

        return new SectionTelemetrySnapshot(
            Context: context,
            TurnIndex: turnIndex,
            Sections: measurements,
            Budget: summary);
    }

    private static double ComputeUsedRatio(SectionBudgetContext budget, int usedChars, int? usedTokens)
    {
        if (budget.MaxTokens is int targetTok && budget.TokenCounter is not null && usedTokens is int used)
        {
            return targetTok == 0 ? 0d : (double)used / targetTok;
        }

        if (budget.MaxChars is int targetCh)
        {
            return targetCh == 0 ? 0d : (double)usedChars / targetCh;
        }

        return 0d;
    }

    private static string ExtractText(Section section) => section.Payload switch
    {
        TextPayload t => t.Value,
        TurnPayload t => t.Turn.Text,
        ToolsPayload t => SummariseTools(t.Tools),
        ResponseFormatPayload r => r.Spec.Schema.GetRawText(),
        _ => string.Empty,
    };

    private static string SummariseTools(IReadOnlyList<ITool> tools)
    {
        if (tools.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var tool in tools)
        {
            sb.Append(tool.Name).Append(' ').Append(tool.Description).Append(' ').Append(tool.ParametersSchema.GetRawText()).Append(' ');
        }

        return sb.ToString();
    }
}
