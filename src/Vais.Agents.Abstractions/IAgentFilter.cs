// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Middleware around a single agent turn, called in registration order around the
/// provider invocation. Stack-neutral replacement for SK's per-kernel filter attach.
/// </summary>
/// <remarks>
/// <para>
/// Execution contract: the runtime constructs a chain <c>f1 → f2 → ... → provider</c>
/// and invokes <see cref="InvokeAsync"/> on the head. Each filter decides whether to
/// await the <c>next</c> delegate; skipping it short-circuits the turn (use with care —
/// the caller still expects a <see cref="CompletionResponse"/>).
/// </para>
/// <para>
/// Filters must be reentrant — a single instance may be invoked concurrently across
/// agent turns.
/// </para>
/// </remarks>
public interface IAgentFilter
{
    /// <summary>
    /// Run this filter. Await <paramref name="next"/> to defer to the next filter in
    /// the chain (and ultimately the provider). Throw to surface errors — the runtime
    /// does not silence filter exceptions.
    /// </summary>
    /// <param name="request">The request about to be sent. Mutation is allowed but advised against.</param>
    /// <param name="next">Invoke to run the remainder of the pipeline.</param>
    /// <param name="cancellationToken">Cancels the whole turn.</param>
    Task<CompletionResponse> InvokeAsync(
        CompletionRequest request,
        Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
        CancellationToken cancellationToken);
}
