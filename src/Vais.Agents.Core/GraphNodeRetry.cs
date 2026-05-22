// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Vais.Agents;

/// <summary>
/// Executes a graph node body with optional retry per <see cref="GraphNodeRetryPolicy"/>. Retries
/// every failure except the terminal set (cancellation, guardrail denial, budget exhaustion,
/// interrupts), with exponential backoff capped at the policy's maximum, logging one WARN per retry.
/// With a null policy (or <c>MaxAttempts &lt;= 1</c>) the body runs exactly once. Shared by the
/// in-process and MAF orchestrators so retry semantics stay identical across both paths.
/// </summary>
internal static class GraphNodeRetry
{
    /// <param name="policy">Retry policy; null or single-attempt = run once, no retry.</param>
    /// <param name="runId">Run correlation id (for the WARN log).</param>
    /// <param name="nodeId">Node id (for the WARN log).</param>
    /// <param name="body">The node body; receives the 1-based attempt number and a token.</param>
    /// <param name="logger">Logger for per-attempt WARN lines.</param>
    /// <param name="ct">Cancellation token; honored during the backoff delay.</param>
    internal static async ValueTask<T> ExecuteAsync<T>(
        GraphNodeRetryPolicy? policy,
        string runId,
        string nodeId,
        Func<int, CancellationToken, ValueTask<T>> body,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (policy is null || policy.MaxAttempts <= 1)
        {
            return await body(1, ct).ConfigureAwait(false);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await body(attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < policy.MaxAttempts && IsRetryable(ex))
            {
                var delay = ComputeBackoff(policy, attempt);
                logger.LogWarning(ex,
                    "Graph node retry. run_id={RunId} node_id={NodeId} attempt={Attempt}/{MaxAttempts} delay_ms={DelayMs} error={ErrorType}",
                    runId, nodeId, attempt, policy.MaxAttempts, (long)delay.TotalMilliseconds, ex.GetType().Name);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Retryable unless in the terminal set (mirrors <c>StatefulAiAgent</c>'s exclusions). A classified
    /// error (<see cref="IClassifiedAgentError"/>, e.g. a container plugin failure) defers to its own
    /// transient flag, so gateway/tool/timeout failures retry while internal errors fail the node.
    /// </summary>
    internal static bool IsRetryable(Exception ex)
    {
        if (ex is OperationCanceledException
            or AgentGuardrailDeniedException
            or AgentBudgetExceededException
            or AgentInterruptedException)
        {
            return false;
        }
        if (ex is IClassifiedAgentError classified)
        {
            return classified.IsTransient;
        }
        return true;
    }

    /// <summary>Exponential backoff for the delay before attempt <paramref name="attempt"/>+1, capped.</summary>
    internal static TimeSpan ComputeBackoff(GraphNodeRetryPolicy policy, int attempt)
    {
        var seconds = policy.InitialBackoffSeconds * Math.Pow(policy.BackoffMultiplier, attempt - 1);
        var capped = Math.Min(seconds, policy.MaxBackoffSeconds);
        return TimeSpan.FromSeconds(Math.Max(0, capped));
    }
}
