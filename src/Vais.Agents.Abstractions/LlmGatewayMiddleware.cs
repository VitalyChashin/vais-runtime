// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Base class for LLM gateway middleware. Covers both the non-streaming
/// (<see cref="IAgentFilter"/>) and streaming (<see cref="IStreamingAgentFilter"/>)
/// paths via a single class. Subclasses override only the hooks they need;
/// all defaults are pass-through / no-op.
/// </summary>
/// <remarks>
/// Instances must be reentrant — do not store per-call state in fields.
/// Use local variables inside the virtual method body or closure variables
/// in <see cref="InvokeStreamAsync"/> for per-call state.
/// </remarks>
public abstract class LlmGatewayMiddleware : IAgentFilter, IStreamingAgentFilter
{
    /// <summary>
    /// Override to inspect or mutate the request / response on the non-streaming path.
    /// Short-circuit by returning a synthetic response without calling <paramref name="next"/>.
    /// </summary>
    protected virtual Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
        => next(request, cancellationToken);

    /// <summary>
    /// Override to wrap the streaming provider call.
    /// Short-circuit by yielding synthetic deltas and <c>yield break</c>ing without calling <paramref name="next"/>.
    /// </summary>
    protected virtual IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => next(request, cancellationToken);

    /// <summary>
    /// Override to transform each streaming delta in-flight.
    /// </summary>
    protected virtual ValueTask<CompletionUpdate> OnDeltaAsync(
        CompletionUpdate update,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(update);

    /// <summary>
    /// Override to observe the fully-accumulated stream response (e.g. for logging or metrics).
    /// The originating request is not available here — capture it in an <see cref="InvokeStreamAsync"/> closure if needed.
    /// </summary>
    protected virtual ValueTask OnStreamCompleteAsync(
        CompletionResponse final,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    // ── Explicit IAgentFilter ────────────────────────────────────────────────

    Task<CompletionResponse> IAgentFilter.InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken)
        => InvokeAsync(request, next, cancellationToken);

    // ── Explicit IStreamingAgentFilter ───────────────────────────────────────

    IAsyncEnumerable<CompletionUpdate> IStreamingAgentFilter.InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
        CancellationToken cancellationToken)
        => InvokeStreamAsync(request, next, cancellationToken);

    ValueTask<CompletionUpdate> IStreamingAgentFilter.OnStreamDeltaAsync(
        CompletionUpdate update,
        CancellationToken cancellationToken)
        => OnDeltaAsync(update, cancellationToken);

    ValueTask IStreamingAgentFilter.OnStreamCompleteAsync(
        CompletionResponse final,
        CancellationToken cancellationToken)
        => OnStreamCompleteAsync(final, cancellationToken);
}
