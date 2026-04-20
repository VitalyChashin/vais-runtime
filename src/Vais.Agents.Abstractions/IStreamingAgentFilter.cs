// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Pipeline hook for streaming turns. Three override points: <see cref="InvokeAsync"/>
/// wraps the provider call (mirrors <see cref="IAgentFilter"/>'s around-provider role
/// on the non-streaming path); <see cref="OnStreamDeltaAsync"/> transforms each
/// <see cref="CompletionUpdate"/> as it flows from the provider to the caller; and
/// <see cref="OnStreamCompleteAsync"/> observes the accumulated response once the
/// stream drains.
/// </summary>
/// <remarks>
/// <para>
/// <b>When to override which.</b> Override <see cref="InvokeAsync"/> when you need to
/// inspect/mutate the <see cref="CompletionRequest"/>, short-circuit the call (e.g.
/// return cached chunks without invoking the provider), or deny the call outright.
/// Override <see cref="OnStreamDeltaAsync"/> to transform individual deltas (PII
/// scrubbing, telemetry). Override <see cref="OnStreamCompleteAsync"/> for
/// end-of-stream validation or accumulator inspection. A single filter can override
/// any combination of the three.
/// </para>
/// <para>
/// <b>Composition.</b> The agent builds a chain <c>f1 → f2 → ... → provider</c> over
/// <see cref="InvokeAsync"/>, iterates the resulting stream, and fires
/// <see cref="OnStreamDeltaAsync"/> on every filter (in registration order) for each
/// delta before yielding to the caller. <see cref="OnStreamCompleteAsync"/> fires on
/// every filter at end of stream, in order, before output guardrails. The per-delta
/// and terminal hooks are agent-driven — filter authors override them without wiring
/// the iteration themselves.
/// </para>
/// <para>
/// <b>Short-circuiting.</b> An <see cref="InvokeAsync"/> override that <c>yield return</c>s
/// synthetic updates and then <c>yield break</c>s without invoking <c>next</c> bypasses
/// the provider entirely. The agent still calls <see cref="OnStreamDeltaAsync"/> on the
/// filter's own yielded deltas — the downstream chain sees them exactly as it would
/// provider-produced ones.
/// </para>
/// <para>
/// Exceptions from any method propagate and fail the turn — same discipline as
/// provider exceptions. <c>StatefulAiAgent</c> captures the failure, reports usage
/// with <c>Succeeded = false</c>, emits <see cref="TurnFailed"/>, and rethrows.
/// Filter-domain exceptions (<see cref="AgentGuardrailDeniedException"/>,
/// <see cref="AgentBudgetExceededException"/>, <see cref="AgentInterruptedException"/>,
/// <see cref="OperationCanceledException"/>) are not subject to the streaming retry
/// pipeline; the agent surfaces them to the caller immediately.
/// </para>
/// </remarks>
public interface IStreamingAgentFilter
{
    /// <summary>
    /// Wrap the streaming provider call. Await <paramref name="next"/> to defer to the
    /// rest of the chain (and ultimately the provider); skip it to short-circuit the
    /// turn with filter-produced deltas. Default: passes straight through to
    /// <paramref name="next"/>.
    /// </summary>
    /// <param name="request">
    /// Request about to be sent. The default implementation passes it through
    /// unmodified; overrides may rewrite and pass the new instance to
    /// <paramref name="next"/>.
    /// </param>
    /// <param name="next">
    /// Invoke to run the remainder of the pipeline. The returned enumerable is a
    /// cold stream — iteration starts the downstream work.
    /// </param>
    /// <param name="cancellationToken">Cancels the whole turn.</param>
    IAsyncEnumerable<CompletionUpdate> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => next(request, cancellationToken);

    /// <summary>Transform a single delta before it's yielded to the caller. Default: pass-through.</summary>
    ValueTask<CompletionUpdate> OnStreamDeltaAsync(CompletionUpdate update, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(update);

    /// <summary>
    /// Called once after the stream has fully drained, with the accumulated response (text
    /// plus any provider-reported metadata). Runs before output guardrails. Default: no-op.
    /// </summary>
    ValueTask OnStreamCompleteAsync(CompletionResponse final, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
