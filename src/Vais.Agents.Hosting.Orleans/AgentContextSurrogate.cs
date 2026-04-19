// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans serialisation surrogate for <see cref="AgentContext"/>.
/// </summary>
[GenerateSerializer]
public struct AgentContextSurrogate
{
    /// <summary>Optional user id.</summary>
    [Id(0)]
    public string? UserId;

    /// <summary>Optional tenant id.</summary>
    [Id(1)]
    public string? TenantId;

    /// <summary>Optional correlation id for cross-service tracing.</summary>
    [Id(2)]
    public string? CorrelationId;

    /// <summary>Optional stable agent name.</summary>
    [Id(3)]
    public string? AgentName;

    /// <summary>Optional run id stamped by <c>StatefulAiAgent</c> for durable execution (v0.5+).</summary>
    [Id(4)]
    public string? RunId;
}

/// <summary>
/// Converts between <see cref="AgentContext"/> and its Orleans-serialisable surrogate.
/// </summary>
[RegisterConverter]
public sealed class AgentContextSurrogateConverter : IConverter<AgentContext, AgentContextSurrogate>
{
    /// <inheritdoc />
    public AgentContext ConvertFromSurrogate(in AgentContextSurrogate surrogate) =>
        new AgentContext(
            UserId: surrogate.UserId,
            TenantId: surrogate.TenantId,
            CorrelationId: surrogate.CorrelationId,
            AgentName: surrogate.AgentName)
        {
            RunId = surrogate.RunId,
        };

    /// <inheritdoc />
    public AgentContextSurrogate ConvertToSurrogate(in AgentContext value) =>
        new()
        {
            UserId = value.UserId,
            TenantId = value.TenantId,
            CorrelationId = value.CorrelationId,
            AgentName = value.AgentName,
            RunId = value.RunId,
        };
}
