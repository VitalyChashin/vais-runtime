// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Base class for error-interceptor seam handlers. Fires once when an agent turn or graph node
/// fails, on the failure path, before the failure event is built and re-thrown. A handler may
/// observe the failure (audit, alert) and optionally rewrite the user-facing
/// <see cref="ErrorContext.ErrorMessage"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a post-error <em>hook</em>, not a <c>next</c>-wrapping middleware: there is no
/// continuation and no pre/post pair. Multiple interceptors run in ascending priority; each sees
/// the message as left by the previous one (sequential fold).
/// </para>
/// <para>
/// <b>It cannot break diagnosability (P9).</b> An interceptor may not suppress the failure (the
/// exception still propagates) and may not change <see cref="ErrorContext.ErrorType"/> — the failure
/// event always carries the original type, and the structured ERROR log (with run/node id + stack)
/// is written <em>before</em> the interceptor runs. Only the human-facing message is replaceable.
/// </para>
/// <para>Instances must be reentrant — no per-call state in fields.</para>
/// </remarks>
public abstract class ErrorInterceptor
{
    /// <summary>
    /// Observe a failure and optionally return a replacement user-facing message.
    /// The default is observe-only.
    /// </summary>
    public virtual Task<ErrorOutcome> OnErrorAsync(ErrorContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(ErrorOutcome.Observe);
}

/// <summary>
/// The failure passed to an <see cref="ErrorInterceptor"/>.
/// </summary>
/// <param name="AgentId">Agent the failure belongs to (the failed node's agent, for a graph node).</param>
/// <param name="RunId">Run id when set; null for a stateless turn.</param>
/// <param name="NodeId">Graph node id when the failure is inside a node; null for a plain agent turn.</param>
/// <param name="ErrorType">
/// The original error type name. Immutable from an interceptor's perspective — the failure event
/// always carries this value (P9).
/// </param>
/// <param name="ErrorMessage">The current human-facing message; the chain may replace it.</param>
public sealed record ErrorContext(
    string AgentId,
    string? RunId,
    string? NodeId,
    string ErrorType,
    string ErrorMessage);

/// <summary>
/// Outcome of an <see cref="ErrorInterceptor.OnErrorAsync"/> call. A non-null <see cref="Message"/>
/// replaces the surfaced error message; null leaves it unchanged (observe-only).
/// </summary>
public sealed record ErrorOutcome(string? Message = null)
{
    /// <summary>Observe-only — leaves the message unchanged.</summary>
    public static readonly ErrorOutcome Observe = new((string?)null);
}

/// <summary>
/// Runs an ordered <see cref="ErrorInterceptor"/> chain over a failure, folding the user-facing
/// message: each interceptor sees the message as left by the previous one. Returns the final
/// message. Never changes <see cref="ErrorContext.ErrorType"/> or suppresses the failure — the
/// caller still emits the failure event and re-throws (P9).
/// </summary>
public static class ErrorInterceptorChain
{
    /// <summary>Fold the chain over <paramref name="context"/> and return the resulting message.</summary>
    public static async Task<string> RunAsync(
        IReadOnlyList<ErrorInterceptor> interceptors,
        ErrorContext context,
        CancellationToken cancellationToken = default)
    {
        var message = context.ErrorMessage;
        for (var i = 0; i < interceptors.Count; i++)
        {
            var outcome = await interceptors[i]
                .OnErrorAsync(context with { ErrorMessage = message }, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(outcome.Message))
                message = outcome.Message!;
        }
        return message;
    }
}
