// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Streaming counterpart to <see cref="ICompletionProvider"/>. Providers that can
/// stream assistant text incrementally implement this alongside the non-streaming
/// interface.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="ICompletionProvider"/> so that non-streaming providers
/// (fakes, legacy adapters, batch-only backends) stay valid without implementing a
/// stub that throws. Consumers detect streaming capability by testing
/// <c>provider is IStreamingCompletionProvider</c>; the core's
/// <c>StatefulAiAgent.StreamAsync</c> does exactly that and throws
/// <see cref="InvalidOperationException"/> if the injected provider doesn't implement
/// this interface.
/// </para>
/// <para>
/// A single class may implement both <see cref="ICompletionProvider"/> and
/// <see cref="IStreamingCompletionProvider"/>; both adapter packages ship their
/// provider as that shape so DI can register one concrete type under two services.
/// </para>
/// </remarks>
public interface IStreamingCompletionProvider
{
    /// <summary>
    /// Short, human-friendly identifier for the provider implementation. Must
    /// return the same value as the corresponding <see cref="ICompletionProvider.ProviderName"/>
    /// when a class implements both, so telemetry stays consistent regardless of
    /// which path the caller uses.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Execute a single completion turn and stream <see cref="CompletionUpdate"/>s
    /// back as the provider generates them.
    /// </summary>
    /// <param name="request">Conversation history plus optional knobs.</param>
    /// <param name="cancellationToken">
    /// Cancels the underlying provider call. Cancellation mid-stream stops further
    /// updates from being emitted; already-yielded deltas are not retracted.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Idempotence contract (v0.10+).</b> Implementations must guarantee that any
    /// exception thrown from <see cref="StreamAsync"/>, its synchronous setup before
    /// the first iteration of the returned enumerable, or the first
    /// <c>MoveNextAsync()</c> on the returned async enumerator leaves no observable
    /// side-effect on shared state — underlying HTTP connections, telemetry spans
    /// that the implementation did not also dispose with error status, session
    /// storage, or cached connector state. The agent core is entitled to retry by
    /// constructing a fresh enumerator from a new <see cref="StreamAsync"/> call on
    /// the same provider instance with the same <paramref name="request"/>.
    /// Exceptions raised after the first <see cref="CompletionUpdate"/> is yielded
    /// are <b>not</b> retryable and surface to the caller as-is. In practice both
    /// shipped adapters satisfy this by construction: Semantic Kernel clones its
    /// <c>Kernel</c> when tools are attached, and the Microsoft Agent Framework
    /// adapter constructs a fresh <c>ChatClientAgent</c> per call — no cross-call
    /// connector state exists before the first delta.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);
}
