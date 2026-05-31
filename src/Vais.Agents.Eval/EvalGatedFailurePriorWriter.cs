// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Eval;

/// <summary>
/// Corroboration gate: before a <see cref="RecipeProposalKind.FailurePrior"/> is approved,
/// finds the <see cref="EvalSuiteManifest"/> that covers the prior's target agent and runs it
/// once. Passes when <c>sum(MechanicalFailureCount) &gt;= 1</c> across all cases — confirming
/// that real mechanical failures are occurring for this agent and the prior is not noise
/// (research §11.7-Q3 "updating ≠ improving").
///
/// Fails-open on all infra errors (body-parse failure, missing suite, run start failure,
/// timeout, run-failed) so operator approvals are never silently blocked by eval
/// infrastructure outages.
/// </summary>
public sealed class EvalGatedFailurePriorWriter : IFailurePriorEvalGate
{
    private readonly IEvalSuiteRegistry _registry;
    private readonly IEvalRunLifecycleManager _lifecycle;
    private readonly ILogger<EvalGatedFailurePriorWriter>? _logger;
    private readonly TimeSpan _timeout;
    private readonly string _workspace;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Create the gate.</summary>
    /// <param name="registry">Suite registry to scan for a matching suite.</param>
    /// <param name="lifecycle">Eval run lifecycle manager.</param>
    /// <param name="workspace">Workspace id passed to <see cref="IEvalRunLifecycleManager.StartRunAsync"/>.</param>
    /// <param name="timeout">Poll deadline. Defaults to 5 minutes.</param>
    /// <param name="logger">Optional logger.</param>
    public EvalGatedFailurePriorWriter(
        IEvalSuiteRegistry registry,
        IEvalRunLifecycleManager lifecycle,
        string workspace,
        TimeSpan? timeout = null,
        ILogger<EvalGatedFailurePriorWriter>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        _workspace = workspace;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<(bool Passed, string? Reason)> EvaluateAsync(RecipeProposal prior, CancellationToken ct)
    {
        FailurePriorBody body;
        try
        {
            body = JsonSerializer.Deserialize<FailurePriorBody>(prior.Body)
                   ?? throw new InvalidOperationException("Deserialized to null.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "FP eval gate: failed to parse FailurePriorBody for proposal {ProposalId} — gate passes (fail-open).",
                prior.ProposalId);
            return (true, "body-parse-failed");
        }

        var agentName = body.AgentName;
        var suite = await FindSuiteForAgentAsync(agentName, ct).ConfigureAwait(false);
        if (suite is null)
        {
            _logger?.LogInformation(
                "FP eval gate: no EvalSuite found for agent '{AgentName}' — gate passes.",
                agentName);
            return (true, null);
        }

        string runId;
        try
        {
            runId = await _lifecycle.StartRunAsync(suite.Id, _workspace, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "FP eval gate: failed to start eval run for suite '{SuiteId}' — gate passes (fail-open).",
                suite.Id);
            return (true, "eval-run-start-failed");
        }

        var deadline = DateTimeOffset.UtcNow + _timeout;
        EvalRunDetail? detail;

        while (true)
        {
            detail = await _lifecycle.GetRunDetailAsync(runId, ct).ConfigureAwait(false);
            if (detail is not null)
            {
                var s = detail.Summary.Status;
                if (s is EvalRunStatus.Completed or EvalRunStatus.Failed or EvalRunStatus.Cancelled)
                    break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger?.LogWarning(
                    "FP eval gate: eval run {RunId} timed out for prior {ProposalId} — gate passes (fail-open).",
                    runId, prior.ProposalId);
                return (true, "eval-gate-timeout");
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        if (detail.Summary.Status is EvalRunStatus.Failed or EvalRunStatus.Cancelled)
        {
            _logger?.LogWarning(
                "FP eval gate: eval run {RunId} ended as {Status} — gate passes (fail-open).",
                runId, detail.Summary.Status);
            return (true, "eval-run-did-not-complete");
        }

        var mechanicalTotal = detail.Cases.Sum(c => c.MechanicalFailureCount);
        if (mechanicalTotal >= 1)
        {
            _logger?.LogInformation(
                "FP eval gate: prior {ProposalId} corroborated — {Count} mechanical failure(s) in suite '{SuiteId}' — gate passes.",
                prior.ProposalId, mechanicalTotal, suite.Id);
            return (true, null);
        }

        var reason = $"EvalSuite '{suite.Id}' observed 0 mechanical failures for agent '{agentName}' — prior is ungrounded";
        _logger?.LogWarning("FP eval gate: {Reason}. Rejecting prior {ProposalId}.", reason, prior.ProposalId);
        return (false, reason);
    }

    private async ValueTask<EvalSuiteManifest?> FindSuiteForAgentAsync(string agentName, CancellationToken ct)
    {
        await foreach (var suite in _registry.ListAsync(ct: ct).ConfigureAwait(false))
        {
            if (suite.Spec.AgentId == agentName || suite.Spec.Target?.AgentRef == agentName)
                return suite;
        }
        return null;
    }
}
