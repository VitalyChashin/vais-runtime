// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Drives a multi-agent conversation: given a user task and a set of
/// <see cref="AgentParticipant"/>s, produces a sequence of
/// <see cref="OrchestrationStep"/>s. Stack-neutral replacement for SK's
/// <c>AgentGroupChat</c> and MAF's group-chat / sequential / handoff orchestrations.
/// </summary>
/// <remarks>
/// <para>
/// Implementations differ in how they compose participants (pipeline vs shared
/// group chat vs handoff) and in their termination rules. Core ships two
/// general-purpose implementations (<c>SequentialOrchestrator</c> +
/// <c>RoundRobinOrchestrator</c>); framework-specific orchestrators that wrap
/// SK's <c>AgentGroupChat</c> or MAF's orchestration primitives may land in
/// adapter packages later.
/// </para>
/// </remarks>
public interface IAgentOrchestrator
{
    /// <summary>
    /// Run the orchestration for a single task. Yields one
    /// <see cref="OrchestrationStep"/> per participant turn. The stream completes
    /// when the orchestrator decides there is nothing more to say (per its
    /// termination rules).
    /// </summary>
    /// <param name="task">The user-supplied task / prompt that seeds the conversation.</param>
    /// <param name="cancellationToken">Cancels mid-run. Steps already yielded are not retracted.</param>
    IAsyncEnumerable<OrchestrationStep> RunAsync(
        string task,
        CancellationToken cancellationToken = default);
}
