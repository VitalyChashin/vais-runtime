// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// A step in the platform-side preprocessing pipeline that runs inside the grain turn,
/// before <c>ContainerAgentShim</c> constructs the <c>InvokeRequest</c>.
/// Built-in implementations: <c>HistoryAssembler</c> (Order 0), <c>SystemPromptInjector</c> (Order 10).
/// Custom implementations (e.g. memory injection, policy enforcement) register via
/// <c>services.AddAgentPreprocessor&lt;T&gt;()</c> without modifying the shim.
/// </summary>
public interface IAgentPreprocessor
{
    /// <summary>Execution order. Lower values run first.</summary>
    int Order { get; }

    /// <summary>
    /// Transforms the message list. Returns a new list; does not mutate <paramref name="messages"/>.
    /// If this preprocessor throws, the exception propagates out of the grain turn — do not swallow.
    /// </summary>
    ValueTask<IReadOnlyList<ChatTurn>> ProcessAsync(
        AgentPreprocessorContext context,
        IReadOnlyList<ChatTurn> messages,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only context available to every preprocessor in the chain.
/// </summary>
/// <param name="AgentId">Stable agent identifier.</param>
/// <param name="SessionId">Session / conversation identifier.</param>
/// <param name="Manifest">The agent's manifest as resolved at grain activation.</param>
/// <param name="GrainState">Read-only view of the grain's persisted state.</param>
/// <param name="OperationContext">Ambient context for the current grain turn (run ID, correlation ID, privilege level, etc.).</param>
public sealed record AgentPreprocessorContext(
    string AgentId,
    string SessionId,
    AgentManifest Manifest,
    IAgentGrainStateView GrainState,
    AgentContext OperationContext);
