// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Core;

/// <summary>
/// Well-known diagnostic identifiers for Vais.Agents. Consumers wire these up to their
/// OpenTelemetry pipelines; the core library emits activities against
/// <see cref="ActivitySource"/> and expects meters to be attached by the
/// <c>Vais.Agents.Observability.OpenTelemetry</c> package.
/// </summary>
/// <remarks>
/// Naming follows the OpenTelemetry GenAI semantic conventions (see ADR 0002).
/// If no listener is registered against <see cref="ActivitySource"/>, activity creation
/// is a no-op and pays no allocation — zero-cost by default.
/// </remarks>
public static class AgenticDiagnostics
{
    /// <summary>
    /// ActivitySource name used by <see cref="StatefulAiAgent"/> when emitting per-turn spans.
    /// Consumers register it on a <c>TracerProviderBuilder</c> via <c>.AddSource("Vais.Agents")</c>.
    /// </summary>
    public const string ActivitySourceName = "Vais.Agents";

    /// <summary>
    /// Meter name consumed by <c>OpenTelemetryUsageSink</c>.
    /// Consumers register it on a <c>MeterProviderBuilder</c> via <c>.AddMeter("Vais.Agents")</c>.
    /// </summary>
    public const string MeterName = "Vais.Agents";

    /// <summary>
    /// Activity name for per-attempt spans in the streaming pipeline.
    /// Each retry attempt within Phase 1 (enumerator-open + first MoveNextAsync)
    /// emits a child span under the parent "chat" span.
    /// </summary>
    public const string StreamAttemptActivityName = "stream_attempt";

    /// <summary>
    /// The shared <see cref="System.Diagnostics.ActivitySource"/> instance used by the core
    /// runtime and available for filters or adapters that need to create child spans.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}

/// <summary>
/// Centralised OTel GenAI tag names (see ADR 0002) plus VAIS-specific extensions.
/// Using these constants avoids typos and makes a rename a single-file change.
/// </summary>
public static class AgenticTags
{
    // OTel GenAI semantic conventions (experimental).

    /// <summary>Provider identifier (e.g. <c>openai</c>, <c>azure.openai</c>, <c>anthropic</c>).</summary>
    public const string GenAiSystem = "gen_ai.system";

    /// <summary>Operation name (<c>chat</c>, <c>text_completion</c>, <c>embeddings</c>, ...).</summary>
    public const string GenAiOperationName = "gen_ai.operation.name";

    /// <summary>Model the caller asked for.</summary>
    public const string GenAiRequestModel = "gen_ai.request.model";

    /// <summary>Model the provider actually used; may differ from the request when an alias resolves.</summary>
    public const string GenAiResponseModel = "gen_ai.response.model";

    /// <summary>Input / prompt tokens consumed by the request.</summary>
    public const string GenAiUsageInputTokens = "gen_ai.usage.input_tokens";

    /// <summary>Output / completion tokens produced by the response.</summary>
    public const string GenAiUsageOutputTokens = "gen_ai.usage.output_tokens";

    /// <summary>Short token-type discriminator used on the token-usage histogram (<c>input</c> or <c>output</c>).</summary>
    public const string GenAiTokenType = "gen_ai.token.type";

    /// <summary>Short exception type name set on errored operations.</summary>
    public const string ErrorType = "error.type";

    // VAIS-specific extensions (clearly prefixed so they don't collide with anything upstream).

    /// <summary>Human-readable agent identifier (e.g. a DI-registered name or a grain key).</summary>
    public const string AgentName = "vais.agent.name";

    /// <summary>User identifier from <see cref="IAgentContextAccessor"/>.</summary>
    public const string UserId = "vais.user.id";

    /// <summary>Tenant identifier from <see cref="IAgentContextAccessor"/>.</summary>
    public const string TenantId = "vais.tenant.id";

    /// <summary>Correlation identifier threading a single logical operation across multiple turns / services.</summary>
    public const string CorrelationId = "vais.correlation.id";

    /// <summary>Zero-based attempt index for streaming retry attempts (0, 1, 2, ...).</summary>
    public const string StreamAttemptIndex = "vais.stream.attempt.index";

    /// <summary>Phase identifier for streaming attempts (always "retry_boundary" for v0.21).</summary>
    public const string StreamAttemptPhase = "vais.stream.attempt.phase";

    /// <summary>Tool name for <c>tool.call</c> spans emitted by <see cref="DefaultToolCallDispatcher"/>.</summary>
    public const string ToolName = "vais.tool.name";

    /// <summary>Tool call identifier correlating a <c>tool.call</c> span to its LLM-side call ID.</summary>
    public const string ToolCallId = "vais.tool.call_id";

    // RCB (Reasoning Control Block) tags — used by RequestContext propagation and gateway middleware.

    /// <summary>Workspace identifier from <see cref="AgentContext.WorkspaceId"/>.</summary>
    public const string WorkspaceId = "vais.workspace.id";

    /// <summary>Privilege level from <see cref="AgentContext.PrivilegeLevel"/>, stored as <see cref="int"/> in RequestContext.</summary>
    public const string PrivilegeLevel = "vais.privilege.level";

    /// <summary>Autonomy level from <see cref="AgentContext.AutonomyLevel"/>, stored as <see cref="int"/> in RequestContext.</summary>
    public const string AutonomyLevel = "vais.autonomy.level";

    /// <summary>Tool allow-list from <see cref="AgentContext.AllowedTools"/>, stored as <c>ImmutableHashSet&lt;string&gt;</c> in RequestContext.</summary>
    public const string AllowedTools = "vais.allowed_tools";

    /// <summary>Maximum agent-as-tool chain depth from <see cref="AgentContext.MaxChainDepth"/>.</summary>
    public const string MaxChainDepth = "vais.max_chain_depth";
}

/// <summary>
/// Well-known metric instrument names emitted by <c>OpenTelemetryUsageSink</c>. Names follow
/// the OTel GenAI semantic conventions so they line up with MEAI's and MAF's native metrics.
/// </summary>
public static class AgenticMetrics
{
    /// <summary>Histogram of tokens consumed, split by <see cref="AgenticTags.GenAiTokenType"/>.</summary>
    public const string TokenUsage = "gen_ai.client.token.usage";

    /// <summary>Histogram of operation duration in seconds.</summary>
    public const string OperationDuration = "gen_ai.client.operation.duration";
}
