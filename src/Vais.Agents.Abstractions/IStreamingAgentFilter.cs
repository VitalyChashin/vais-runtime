// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Pipeline hook for streaming turns. Lets consumers transform each
/// <see cref="CompletionUpdate"/> as it flows from the provider to the caller, and
/// observe the accumulated response once the stream drains. Closes the v0.3
/// "streaming bypasses filters" gap — the regular <see cref="IAgentFilter"/> chain
/// is request→response-shaped and can't hook individual deltas without buffering
/// the entire stream.
/// </summary>
/// <remarks>
/// <para>
/// Both methods have default no-op implementations (via DIM) so consumers override
/// only what they need: a PII scrubber overrides <see cref="OnStreamDeltaAsync"/>;
/// an end-of-stream validator overrides <see cref="OnStreamCompleteAsync"/>.
/// </para>
/// <para>
/// Exceptions from either method propagate and fail the turn — same discipline as
/// provider exceptions. <c>StatefulAiAgent</c> captures the failure, reports usage
/// with <c>Succeeded = false</c>, emits <see cref="TurnFailed"/>, and rethrows.
/// </para>
/// </remarks>
public interface IStreamingAgentFilter
{
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
