// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Stack-neutral single-turn completion contract.
/// </summary>
/// <remarks>
/// <para>
/// Whatever agent / orchestration features a host AI stack offers (Semantic Kernel's
/// Kernel + plugins, Microsoft Agent Framework's AIAgent + AgentSession, etc.), they
/// are reduced at this boundary to "messages in, one assistant message out". That
/// reduction is the unification point of Vais.Agents — every feature above this
/// interface (history, persistence, identity, observability, multi-agent orchestration)
/// lives in stack-neutral code.
/// </para>
/// <para>
/// Implementations are expected to be thread-safe for concurrent invocations unless
/// otherwise documented.
/// </para>
/// </remarks>
public interface ICompletionProvider
{
    /// <summary>
    /// Short, human-friendly identifier for the provider implementation, e.g. "SemanticKernel"
    /// or "MicrosoftAgentFramework". Used in logs and telemetry tags.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Indicates whether this provider supports wire-level structured output
    /// (<c>response_format: json_schema</c> or equivalent). Default: false.
    /// Providers that support it override this to return true and wire
    /// <see cref="CompletionRequest.ResponseFormat"/> onto the underlying SDK call.
    /// </summary>
    bool SupportsResponseFormat => false;

    /// <summary>
    /// Execute a single completion turn.
    /// </summary>
    /// <param name="request">Conversation history plus optional knobs.</param>
    /// <param name="cancellationToken">Cancels the underlying provider call.</param>
    /// <returns>The assistant response plus provider-side metadata when available.</returns>
    Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default);
}
