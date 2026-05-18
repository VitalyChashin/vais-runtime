// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents.Eval;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans grain that runs an eval suite case-by-case. Each grain turn processes
/// exactly one case (P2). State is persisted so a silo restart resumes from the
/// next unprocessed case.
/// </summary>
public sealed class EvalRunGrain : Grain, IEvalRunGrain
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly IPersistentState<EvalRunGrainState> _state;
    private readonly ILogger<EvalRunGrain> _logger;

    /// <summary>DI ctor.</summary>
    public EvalRunGrain(
        [PersistentState("eval-run", AiAgentGrain.StorageName)] IPersistentState<EvalRunGrainState> state,
        ILogger<EvalRunGrain> logger)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logger);
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask StartAsync(string suiteJson, string workspace, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteJson);
        if (_state.State.Status != EvalRunStatus.Pending)
            return; // idempotent

        var suite = JsonSerializer.Deserialize<EvalSuiteManifest>(suiteJson, JsonOpts)!;
        var evalRunId = this.GetPrimaryKeyString();

        _state.State.SuiteName = suite.Id;
        _state.State.SuiteVersion = suite.Version;
        _state.State.SuiteJson = suiteJson;
        _state.State.TotalCases = suite.Spec.Cases?.Count ?? 0;
        _state.State.Status = EvalRunStatus.Running;
        _state.State.StartedAt = DateTimeOffset.UtcNow;
        _state.State.Workspace = workspace;
        await _state.WriteStateAsync();

        var store = ServiceProvider.GetRequiredService<IEvalResultStore>();
        await store.AppendRunAsync(BuildSummary(evalRunId), ct);

        _ = this.AsReference<IEvalRunGrain>().ProcessNextCaseAsync();
    }

    /// <inheritdoc/>
    public async ValueTask ProcessNextCaseAsync(CancellationToken ct = default)
    {
        var evalRunId = this.GetPrimaryKeyString();

        if (_state.State.Status == EvalRunStatus.Cancelled ||
            _state.State.Status == EvalRunStatus.Completed ||
            _state.State.Status == EvalRunStatus.Failed)
            return;

        var suite = JsonSerializer.Deserialize<EvalSuiteManifest>(_state.State.SuiteJson!, JsonOpts)!;
        var idx = _state.State.CurrentCaseIndex;

        var cases = suite.Spec.Cases ?? [];
        if (idx >= cases.Count)
        {
            await CompleteRunAsync(evalRunId, ct);
            return;
        }

        var @case = cases[idx];
        var caseResult = await ProcessCaseAsync(evalRunId, suite, @case, ct);

        if (caseResult.Status == EvalCaseStatus.Pass) _state.State.PassedCases++;
        else _state.State.FailedCases++;

        _state.State.CurrentCaseIndex++;

        var store = ServiceProvider.GetRequiredService<IEvalResultStore>();
        await store.AppendCaseResultAsync(caseResult, ct);
        await store.AppendRunAsync(BuildSummary(evalRunId), ct);
        await _state.WriteStateAsync();

        PublishProgress(evalRunId, "case-completed", @case.Id, (int)caseResult.Status);

        if (_state.State.CurrentCaseIndex >= cases.Count)
            await CompleteRunAsync(evalRunId, ct);
        else
            _ = this.AsReference<IEvalRunGrain>().ProcessNextCaseAsync();
    }

    /// <inheritdoc/>
    public async ValueTask CancelAsync(CancellationToken ct = default)
    {
        if (_state.State.Status is EvalRunStatus.Completed or EvalRunStatus.Failed or EvalRunStatus.Cancelled)
            return;
        _state.State.Status = EvalRunStatus.Cancelled;
        _state.State.CompletedAt = DateTimeOffset.UtcNow;
        await _state.WriteStateAsync();
        var store = ServiceProvider.GetRequiredService<IEvalResultStore>();
        await store.AppendRunAsync(BuildSummary(this.GetPrimaryKeyString()), ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<EvalCaseResultRecord> ProcessCaseAsync(
        string evalRunId, EvalSuiteManifest suite, EvalCase @case, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var events = new List<AgentEvent>();
        string? agentRunId = null;
        string responseText = string.Empty;
        int? promptTokens = null, completionTokens = null;

        try
        {
            var lifecycle = ServiceProvider.GetRequiredService<IAgentLifecycleManager>();
            var eventBus = ServiceProvider.GetService<IAgentEventBus>();

            // Subscribe to event bus before invoke so we capture all events for this case.
            IDisposable? sub = null;
            if (eventBus is not null)
            {
                sub = eventBus.Subscribe((e, _) =>
                {
                    events.Add(e);
                    return ValueTask.CompletedTask;
                });
            }

            try
            {
                var agentId = suite.Spec.Target?.AgentRef ?? suite.Spec.AgentId;
                var agentVersion = suite.Spec.Target?.AgentVersion ?? "latest";
                var handle = new AgentHandle(agentId!, agentVersion);

                IReadOnlyList<(string Role, string Content)>? history = null;
                if (@case.InitialHistory is { Count: > 0 })
                    history = @case.InitialHistory.Select(t => (t.Role, t.Content)).ToList();

                var effectiveReplay = @case.Replay ?? suite.Spec.ReplayMode;
                var baselineRunId = effectiveReplay == EvalReplayMode.Cached
                    ? suite.Spec.Baseline?.RunId
                    : null;

                var request = new AgentInvocationRequest(@case.Input, InitialHistory: history);

                if (baselineRunId is not null)
                {
                    var contextSetter = ServiceProvider.GetService<IAgentContextSetter>();
                    using var _ = contextSetter?.Push(AgentContext.Empty with { BaselineRunId = baselineRunId });
                    var result = await lifecycle.InvokeAsync(handle, request, ct);
                    responseText = result.Text;
                }
                else
                {
                    var result = await lifecycle.InvokeAsync(handle, request, ct);
                    responseText = result.Text;
                }
            }
            finally
            {
                sub?.Dispose();
            }

            sw.Stop();

            // Extract runId + token counts from captured events
            agentRunId = events.OfType<TurnStarted>().FirstOrDefault()?.Context.RunId;
            var completed = events.OfType<TurnCompleted>().FirstOrDefault();
            if (completed is not null)
            {
                promptTokens = completed.PromptTokens;
                completionTokens = completed.CompletionTokens;
            }

            // Read journal entries for this run
            List<JournalEntry> journalEntries = new();
            if (agentRunId is not null)
            {
                var journal = ServiceProvider.GetService<IAgentJournal>();
                if (journal is not null)
                {
                    await foreach (var entry in journal.ReadAsync(agentRunId, ct))
                        journalEntries.Add(entry);
                }
            }

            var record = new EvalRunRecord(
                AgentRunId: agentRunId ?? string.Empty,
                ResponseText: responseText,
                ResponseJson: null,
                JournalEntries: journalEntries,
                Events: events,
                FinalState: null,
                Duration: sw.Elapsed,
                PromptTokens: promptTokens,
                CompletionTokens: completionTokens);

            // Run assertions
            var registry = ServiceProvider.GetService<IEvalAssertionFactoryRegistry>();
            var assertionResults = new List<EvalAssertionResultRecord>();
            var caseCtx = new EvalCaseContext(@case, suite.Spec, AgentContext.Empty);

            for (var i = 0; i < @case.Assertions.Count; i++)
            {
                var spec = @case.Assertions[i];
                EvalAssertionResultRecord assertResult;
                if (registry is null || !registry.TryGet(spec.Kind, out var factory))
                {
                    _logger.LogWarning("Eval assertion kind '{Kind}' not registered (evalRunId={EvalRunId}, caseId={CaseId})",
                        spec.Kind, evalRunId, @case.Id);
                    assertResult = new EvalAssertionResultRecord(i, spec.Kind, EvalAssertionStatus.Error, null, $"Unknown assertion kind '{spec.Kind}'");
                }
                else
                {
                    try
                    {
                        var assertion = factory.Create(spec.Params ?? default, ServiceProvider);
                        var outcome = await assertion.EvaluateAsync(caseCtx, record, ct);
                        assertResult = new EvalAssertionResultRecord(i, spec.Kind, outcome.Status, outcome.Score, outcome.Reason);

                        if (outcome.Status == EvalAssertionStatus.Fail)
                            _logger.LogWarning("Eval assertion fail: {Kind} in {CaseId}/{EvalRunId} — {Reason}",
                                spec.Kind, @case.Id, evalRunId, outcome.Reason);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Eval assertion error: {Kind} in {CaseId}/{EvalRunId}", spec.Kind, @case.Id, evalRunId);
                        assertResult = new EvalAssertionResultRecord(i, spec.Kind, EvalAssertionStatus.Error, null, ex.Message);
                    }
                }
                assertionResults.Add(assertResult);
            }

            var allPass = assertionResults.All(a => a.Status == EvalAssertionStatus.Pass || a.Status == EvalAssertionStatus.Skipped);
            return new EvalCaseResultRecord(
                evalRunId, @case.Id, agentRunId, startedAt, DateTimeOffset.UtcNow,
                allPass ? EvalCaseStatus.Pass : EvalCaseStatus.Fail,
                responseText, assertionResults);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Eval case error: {CaseId}/{EvalRunId}", @case.Id, evalRunId);
            return new EvalCaseResultRecord(
                evalRunId, @case.Id, agentRunId, startedAt, DateTimeOffset.UtcNow,
                EvalCaseStatus.Error, responseText, Array.Empty<EvalAssertionResultRecord>());
        }
    }

    private async Task CompleteRunAsync(string evalRunId, CancellationToken ct)
    {
        _state.State.Status = EvalRunStatus.Completed;
        _state.State.CompletedAt = DateTimeOffset.UtcNow;
        await _state.WriteStateAsync();
        var store = ServiceProvider.GetRequiredService<IEvalResultStore>();
        await store.AppendRunAsync(BuildSummary(evalRunId), ct);
        PublishProgress(evalRunId, "run-completed", caseId: null, caseStatus: null);
    }

    private void PublishProgress(string evalRunId, string progressKind, string? caseId, int? caseStatus)
    {
        var bus = ServiceProvider.GetService<IAgentEventBus>();
        if (bus is null) return;
        var evt = new EvalRunProgress(DateTimeOffset.UtcNow, AgentContext.Empty, evalRunId, progressKind, caseId, caseStatus);
        // Fire-and-forget — progress delivery is best-effort.
        _ = bus.PublishAsync(evt, CancellationToken.None).AsTask().ContinueWith(
            t => _logger.LogWarning(t.Exception, "Failed to publish EvalRunProgress"),
            default,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Current);
    }

    private EvalRunSummary BuildSummary(string evalRunId) => new(
        evalRunId,
        _state.State.SuiteName ?? string.Empty,
        _state.State.SuiteVersion ?? string.Empty,
        _state.State.StartedAt,
        _state.State.CompletedAt,
        _state.State.Status,
        _state.State.TotalCases,
        _state.State.PassedCases,
        _state.State.FailedCases);
}
