// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans serialisation surrogate for <see cref="BackgroundAgentRunRecord"/>. The Abstractions
/// package is intentionally Orleans-free, so the serialiser lives here alongside its converter.
/// </summary>
[GenerateSerializer]
public struct BackgroundAgentRunRecordSurrogate
{
    /// <summary>Durable handle (child session id).</summary>
    [Id(0)]
    public string Handle;

    /// <summary>Run id of the coordinator that started this sub-run.</summary>
    [Id(1)]
    public string ParentRunId;

    /// <summary>Agent id of the sub-agent.</summary>
    [Id(2)]
    public string ChildAgentId;

    /// <summary>Status stored as int for enum-rename stability.</summary>
    [Id(3)]
    public int Status;

    /// <summary>When the run was enqueued.</summary>
    [Id(4)]
    public DateTimeOffset StartedAt;

    /// <summary>When the run reached a terminal status, or <c>null</c> if still active.</summary>
    [Id(5)]
    public DateTimeOffset? CompletedAt;

    /// <summary>Final text returned by the sub-agent on success, or <c>null</c>.</summary>
    [Id(6)]
    public string? Result;

    /// <summary>Error message on failure, or <c>null</c>.</summary>
    [Id(7)]
    public string? Error;
}

/// <summary>
/// Converts between <see cref="BackgroundAgentRunRecord"/> and its Orleans-serialisable surrogate.
/// </summary>
[RegisterConverter]
public sealed class BackgroundAgentRunRecordSurrogateConverter
    : IConverter<BackgroundAgentRunRecord, BackgroundAgentRunRecordSurrogate>
{
    /// <inheritdoc />
    public BackgroundAgentRunRecord ConvertFromSurrogate(in BackgroundAgentRunRecordSurrogate surrogate) =>
        new(
            Handle: surrogate.Handle,
            ParentRunId: surrogate.ParentRunId,
            ChildAgentId: surrogate.ChildAgentId,
            Status: (BackgroundAgentRunStatus)surrogate.Status,
            StartedAt: surrogate.StartedAt,
            CompletedAt: surrogate.CompletedAt,
            Result: surrogate.Result,
            Error: surrogate.Error);

    /// <inheritdoc />
    public BackgroundAgentRunRecordSurrogate ConvertToSurrogate(in BackgroundAgentRunRecord value) =>
        new()
        {
            Handle = value.Handle,
            ParentRunId = value.ParentRunId,
            ChildAgentId = value.ChildAgentId,
            Status = (int)value.Status,
            StartedAt = value.StartedAt,
            CompletedAt = value.CompletedAt,
            Result = value.Result,
            Error = value.Error,
        };
}
