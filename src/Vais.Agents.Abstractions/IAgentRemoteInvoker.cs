// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Thin invoker for a single remote runtime endpoint.
/// Keeps orchestrators testable by isolating the outbound HTTP surface
/// from <see cref="IAgentLifecycleManager"/>.
/// </summary>
public interface IAgentRemoteInvoker
{
    /// <summary>
    /// Invokes an agent on a remote runtime and returns its result.
    /// </summary>
    /// <param name="runtimeUrl">Absolute http/https base URL of the target runtime.</param>
    /// <param name="handle">Agent identity (id + version). Null version passes through; the remote runtime resolves latest.</param>
    /// <param name="request">Invocation request payload.</param>
    /// <param name="bearerToken">Optional bearer token forwarded from the caller's inbound request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<AgentInvocationResult> InvokeAsync(
        string runtimeUrl,
        AgentHandle handle,
        AgentInvocationRequest request,
        string? bearerToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes an agent on a remote runtime and streams the <see cref="AgentEvent"/> taxonomy
    /// via server-sent events. Yields events until the remote stream ends or the token fires.
    /// </summary>
    /// <remarks>
    /// When the remote runtime does not support streaming (Orleans proxy agents), the call
    /// throws <see cref="RemoteAgentInvocationException"/> with HTTP 501.
    /// </remarks>
    /// <param name="runtimeUrl">Absolute http/https base URL of the target runtime.</param>
    /// <param name="handle">Agent identity (id + version). Null version passes through; the remote runtime resolves latest.</param>
    /// <param name="request">Invocation request payload.</param>
    /// <param name="bearerToken">Optional bearer token forwarded from the caller's inbound request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<AgentEvent> StreamAsync(
        string runtimeUrl,
        AgentHandle handle,
        AgentInvocationRequest request,
        string? bearerToken,
        CancellationToken cancellationToken = default);
}
