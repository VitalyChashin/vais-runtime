// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vais.Agents.Eval.Continuous;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// <see cref="IRunCompletionListener"/> that applies deterministic-hash sampling
/// against every completed production run and enqueues matching runs to the
/// corresponding <see cref="IContinuousScoringGrain"/> for assertion evaluation.
/// </summary>
internal sealed class ProductionSampler : IRunCompletionListener
{
    private readonly IContinuousSuiteIndex _index;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<ProductionSampler> _logger;

    public ProductionSampler(
        IContinuousSuiteIndex index,
        IGrainFactory grainFactory,
        ILogger<ProductionSampler> logger)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _index = index;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask OnRunCompletedAsync(RunCompletionSignal signal, CancellationToken ct)
    {
        foreach (var suite in _index.SuitesFor(signal.WorkspaceId, signal.AgentRef, signal.GraphRef))
        {
            if (!ShouldSample(signal.AgentRunId, suite.SamplingRate))
                continue;

            _logger.LogDebug("Continuous eval sampled run {RunId} for suite {SuiteId}", signal.AgentRunId, suite.SuiteId);
            var grain = _grainFactory.GetGrain<IContinuousScoringGrain>(suite.SuiteId);
            await grain.EnqueueSampleAsync(signal.AgentRunId, signal.CompletedAt, signal.AssistantText, signal.FinalState);
        }
    }

    /// <summary>
    /// Deterministic hash-based sampling. Identical for the same <paramref name="runId"/>
    /// regardless of which silo evaluates it, so N silos produce N identical enqueue
    /// calls that the grain deduplicates via unique constraint on (eval_run_id, case_id).
    /// </summary>
    internal static bool ShouldSample(string runId, double rate)
    {
        var h = XxHash64.HashToUInt64(MemoryMarshal.AsBytes(runId.AsSpan()));
        return (double)h / ulong.MaxValue < rate;
    }
}
