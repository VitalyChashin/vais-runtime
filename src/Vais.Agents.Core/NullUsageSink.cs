// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Default no-op <see cref="IUsageSink"/>. Used when a consumer hasn't registered
/// any telemetry sink. Never throws, never allocates.
/// </summary>
public sealed class NullUsageSink : IUsageSink
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly NullUsageSink Instance = new();

    private NullUsageSink() { }

    /// <inheritdoc />
    public ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default) => default;
}
