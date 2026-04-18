// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Polly;

namespace Vais2.Agents.Core;

/// <summary>
/// Construction-time options for <see cref="StatefulAiAgent"/>. Every field is
/// optional and has a sensible default; consumers override what they care about.
/// </summary>
public sealed class StatefulAgentOptions
{
    /// <summary>
    /// Optional stable name for this agent. Surfaces in telemetry via
    /// <see cref="UsageRecord.AgentName"/>.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// System instruction prepended to every turn. Mutable after construction via
    /// <see cref="IAiAgent.SystemPrompt"/>.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Ordered filter chain. Filters run in the order given, outermost first.
    /// Default: empty (no filters).
    /// </summary>
    public IReadOnlyList<IAgentFilter> Filters { get; init; } = Array.Empty<IAgentFilter>();

    /// <summary>
    /// Usage telemetry sink. Default: <see cref="NullUsageSink.Instance"/>.
    /// </summary>
    public IUsageSink? UsageSink { get; init; }

    /// <summary>
    /// Ambient context accessor. Default: a private <see cref="AsyncLocalAgentContextAccessor"/>.
    /// </summary>
    public IAgentContextAccessor? ContextAccessor { get; init; }

    /// <summary>
    /// Resilience pipeline wrapping the provider call. When null, the core uses a
    /// default pipeline with 3 retries and exponential back-off — matching the
    /// behaviour of the previous <c>AiAgent&lt;T&gt;.CallFunction</c> in VAIS2.
    /// </summary>
    public ResiliencePipeline? ResiliencePipeline { get; init; }

    /// <summary>
    /// Tools made available to the model on every turn. When set, the registry's
    /// <see cref="IToolRegistry.Tools"/> is attached to each <see cref="CompletionRequest"/>
    /// and the adapter is expected to advertise them to its underlying SDK with
    /// auto-invocation enabled. Null means the agent operates without tools.
    /// </summary>
    public IToolRegistry? ToolRegistry { get; init; }

    /// <summary>
    /// Optional history to seed the agent with at construction. Intended for hosts that
    /// persist chat state externally (e.g. Orleans grains, a database-backed history store)
    /// and reconstruct <see cref="StatefulAiAgent"/> on activation. The supplied turns are
    /// copied into the agent's internal history list in order; callers may safely hand in
    /// a snapshot and mutate their source afterwards.
    /// </summary>
    public IReadOnlyList<ChatTurn>? InitialHistory { get; init; }
}
