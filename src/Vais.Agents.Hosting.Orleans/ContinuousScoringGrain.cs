// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents.Eval;
using Vais.Agents.Eval.Continuous;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain (keyed by suite id) that manages rolling eval-run windows and
/// scores sampled production runs. One grain turn per sample (P2). Grain state is
/// persisted so window rotation and the pending queue survive silo restart.
/// </summary>
public sealed class ContinuousScoringGrain : Grain, IContinuousScoringGrain
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IPersistentState<ContinuousScoringGrainState> _state;
    private readonly ILogger<ContinuousScoringGrain> _logger;

    /// <summary>DI ctor.</summary>
    public ContinuousScoringGrain(
        [PersistentState("continuous-scoring", AiAgentGrain.StorageName)] IPersistentState<ContinuousScoringGrainState> state,
        ILogger<ContinuousScoringGrain> logger)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask EnqueueSampleAsync(
        string productionRunId,
        DateTimeOffset completedAt,
        string? assistantText,
        IReadOnlyDictionary<string, JsonElement>? finalState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productionRunId);

        var finalStateJson = finalState is null
            ? null
            : JsonSerializer.Serialize(finalState, JsonOpts);

        _state.State.PendingQueue.Add(new PendingSample
        {
            ProductionRunId = productionRunId,
            CompletedAt = completedAt,
            AssistantText = assistantText,
            FinalStateJson = finalStateJson,
        });
        await _state.WriteStateAsync();

        // Self-schedule processing as next grain turn (P2).
        _ = this.AsReference<IContinuousScoringGrain>().ProcessNextAsync();
    }

    /// <inheritdoc/>
    public async ValueTask ProcessNextAsync()
    {
        if (_state.State.PendingQueue.Count == 0)
            return;

        var sample = _state.State.PendingQueue[0];
        _state.State.PendingQueue.RemoveAt(0);

        var suiteId = this.GetPrimaryKeyString();
        try
        {
            await ProcessSampleAsync(suiteId, sample);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Continuous scoring error for suite {SuiteId} run {RunId}", suiteId, sample.ProductionRunId);
        }

        await _state.WriteStateAsync();

        // If more items in queue, schedule next turn.
        if (_state.State.PendingQueue.Count > 0)
            _ = this.AsReference<IContinuousScoringGrain>().ProcessNextAsync();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task ProcessSampleAsync(string suiteId, PendingSample sample)
    {
        var suite = await ResolveSuiteAsync(suiteId);
        if (suite is null)
        {
            _logger.LogWarning("ContinuousScoringGrain: suite {SuiteId} not found — discarding sample {RunId}", suiteId, sample.ProductionRunId);
            return;
        }

        EnsureWindow(suite, sample.CompletedAt);

        var store = ServiceProvider.GetRequiredService<IEvalResultStore>();
        var evalRunId = _state.State.CurrentWindowEvalRunId!;

        // Rehydrate run record from journal + signal data.
        var record = await BuildRunRecordAsync(sample);

        // Run suite-level assertions.
        var registry = ServiceProvider.GetService<IEvalAssertionFactoryRegistry>();
        var assertionResults = new List<EvalAssertionResultRecord>();
        var assertions = suite.Spec.Assertions ?? Array.Empty<EvalAssertion>();

        // Synthetic case for context; no per-case assertions.
        var syntheticCase = new EvalCase { Id = $"prod-{sample.ProductionRunId}", Input = string.Empty };
        var caseCtx = new EvalCaseContext(syntheticCase, suite.Spec, AgentContext.Empty);

        for (var i = 0; i < assertions.Count; i++)
        {
            var spec = assertions[i];
            EvalAssertionResultRecord assertResult;
            if (registry is null || !registry.TryGet(spec.Kind, out var factory))
            {
                _logger.LogWarning("Continuous eval: assertion kind '{Kind}' not registered (suite={SuiteId}, run={RunId})",
                    spec.Kind, suiteId, sample.ProductionRunId);
                assertResult = new EvalAssertionResultRecord(i, spec.Kind, EvalAssertionStatus.Error, null, $"Unknown assertion kind '{spec.Kind}'");
            }
            else
            {
                try
                {
                    var assertion = factory.Create(spec.Params ?? default, ServiceProvider);
                    var outcome = await assertion.EvaluateAsync(caseCtx, record, CancellationToken.None);
                    assertResult = new EvalAssertionResultRecord(i, spec.Kind, outcome.Status, outcome.Score, outcome.Reason);

                    if (outcome.Status == EvalAssertionStatus.Fail)
                        _logger.LogWarning("Continuous eval assertion fail: {Kind} suite={SuiteId} run={RunId} — {Reason}",
                            spec.Kind, suiteId, sample.ProductionRunId, outcome.Reason);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Continuous eval assertion error: {Kind} suite={SuiteId} run={RunId}", spec.Kind, suiteId, sample.ProductionRunId);
                    assertResult = new EvalAssertionResultRecord(i, spec.Kind, EvalAssertionStatus.Error, null, ex.Message);
                }
            }
            assertionResults.Add(assertResult);
        }

        var allPass = assertionResults.All(a => a.Status is EvalAssertionStatus.Pass or EvalAssertionStatus.Skipped);
        var caseStatus = allPass ? EvalCaseStatus.Pass : EvalCaseStatus.Fail;

        var caseResult = new EvalCaseResultRecord(
            evalRunId,
            $"prod-{sample.ProductionRunId}",
            sample.ProductionRunId,
            sample.CompletedAt,
            DateTimeOffset.UtcNow,
            caseStatus,
            sample.AssistantText,
            assertionResults,
            ProductionRunId: sample.ProductionRunId);

        await store.AppendCaseResultAsync(caseResult);

        _state.State.CurrentWindowSampledCount++;
        if (allPass) _state.State.CurrentWindowPassedCount++;
        else _state.State.CurrentWindowFailedCount++;

        // Update the window-level run summary.
        var sampling = suite.Spec.Sampling!;
        var windowSummary = new EvalRunSummary(
            evalRunId, suite.Id, suite.Version,
            _state.State.CurrentWindowStart!.Value, null,
            EvalRunStatus.Running,
            _state.State.CurrentWindowSampledCount,
            _state.State.CurrentWindowPassedCount,
            _state.State.CurrentWindowFailedCount,
            Source: "continuous",
            WindowStart: _state.State.CurrentWindowStart,
            WindowEnd: _state.State.CurrentWindowEnd);
        await store.AppendRunAsync(windowSummary);

        // Emit metrics.
        var metrics = ServiceProvider.GetService<IContinuousScoringMetricSink>();
        if (metrics is not null)
        {
            metrics.RecordSample(suiteId, caseStatus == EvalCaseStatus.Pass ? "Pass" : "Fail");
            foreach (var ar in assertionResults)
            {
                var score = ar.Score ?? (ar.Status == EvalAssertionStatus.Pass ? 1.0 : 0.0);
                metrics.ObserveAssertionScore(suiteId, ar.Kind, score);
            }
            metrics.SetWindowSampledCount(suiteId, _state.State.CurrentWindowSampledCount);
            var remaining = (_state.State.CurrentWindowEnd!.Value - DateTimeOffset.UtcNow).TotalSeconds;
            metrics.SetWindowSecondsRemaining(suiteId, Math.Max(0, remaining));
        }
    }

    private void EnsureWindow(EvalSuiteManifest suite, DateTimeOffset sampleTime)
    {
        var sampling = suite.Spec.Sampling!;
        var now = sampleTime;

        // Rotate window if the current one has expired.
        if (_state.State.CurrentWindowEnd.HasValue && now >= _state.State.CurrentWindowEnd.Value)
        {
            // Close the old window.
            CloseCurrentWindow(suite);
        }

        // Open a new window if none exists.
        if (_state.State.CurrentWindowEvalRunId is null)
        {
            var windowStart = now;
            var windowEnd = now + sampling.WindowDuration;
            var evalRunId = $"ceval-{suite.Id}-{windowStart:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            _state.State.CurrentWindowEvalRunId = evalRunId;
            _state.State.CurrentWindowStart = windowStart;
            _state.State.CurrentWindowEnd = windowEnd;
            _state.State.CurrentWindowSampledCount = 0;
            _state.State.CurrentWindowPassedCount = 0;
            _state.State.CurrentWindowFailedCount = 0;

            var store = ServiceProvider.GetRequiredService<IEvalResultStore>();
            var summary = new EvalRunSummary(
                evalRunId, suite.Id, suite.Version,
                windowStart, null,
                EvalRunStatus.Running,
                0, 0, 0,
                Source: "continuous",
                WindowStart: windowStart,
                WindowEnd: windowEnd);
            // Fire-and-forget — we'll update it after each sample.
            _ = store.AppendRunAsync(summary).AsTask();
        }
    }

    private void CloseCurrentWindow(EvalSuiteManifest suite)
    {
        var evalRunId = _state.State.CurrentWindowEvalRunId;
        if (evalRunId is null) return;

        var store = ServiceProvider.GetRequiredService<IEvalResultStore>();
        var summary = new EvalRunSummary(
            evalRunId, suite.Id, suite.Version,
            _state.State.CurrentWindowStart!.Value,
            DateTimeOffset.UtcNow,
            EvalRunStatus.Completed,
            _state.State.CurrentWindowSampledCount,
            _state.State.CurrentWindowPassedCount,
            _state.State.CurrentWindowFailedCount,
            Source: "continuous",
            WindowStart: _state.State.CurrentWindowStart,
            WindowEnd: _state.State.CurrentWindowEnd);
        _ = store.AppendRunAsync(summary).AsTask();

        // Reset for next window.
        _state.State.CurrentWindowEvalRunId = null;
        _state.State.CurrentWindowStart = null;
        _state.State.CurrentWindowEnd = null;
        _state.State.CurrentWindowSampledCount = 0;
        _state.State.CurrentWindowPassedCount = 0;
        _state.State.CurrentWindowFailedCount = 0;
    }

    private async Task<EvalRunRecord> BuildRunRecordAsync(PendingSample sample)
    {
        var journalEntries = new List<JournalEntry>();
        var journal = ServiceProvider.GetService<IAgentJournal>();
        if (journal is not null && !string.IsNullOrEmpty(sample.ProductionRunId))
        {
            await foreach (var entry in journal.ReadAsync(sample.ProductionRunId))
                journalEntries.Add(entry);
        }

        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? finalState = null;
        if (sample.FinalStateJson is not null)
        {
            try
            {
                finalState = JsonSerializer.Deserialize<IReadOnlyDictionary<string, System.Text.Json.JsonElement>>(
                    sample.FinalStateJson, JsonOpts);
            }
            catch { /* malformed JSON — skip */ }
        }

        return new EvalRunRecord(
            AgentRunId: sample.ProductionRunId,
            ResponseText: sample.AssistantText ?? string.Empty,
            ResponseJson: null,
            JournalEntries: journalEntries,
            Events: Array.Empty<AgentEvent>(),
            FinalState: finalState,
            Duration: TimeSpan.Zero,
            PromptTokens: null,
            CompletionTokens: null);
    }

    private async Task<EvalSuiteManifest?> ResolveSuiteAsync(string suiteId)
    {
        if (_state.State.SuiteJson is not null)
        {
            try { return JsonSerializer.Deserialize<EvalSuiteManifest>(_state.State.SuiteJson, JsonOpts); }
            catch { /* stale or malformed — fall through to registry */ }
        }

        var registry = ServiceProvider.GetService<IEvalSuiteRegistry>();
        if (registry is null) return null;
        var manifest = await registry.GetAsync(suiteId);
        if (manifest is not null)
            _state.State.SuiteJson = JsonSerializer.Serialize(manifest, JsonOpts);
        return manifest;
    }
}
