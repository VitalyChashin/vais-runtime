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
    IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);
}
