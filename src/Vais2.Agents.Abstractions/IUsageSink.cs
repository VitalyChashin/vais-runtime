// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents;

/// <summary>
/// Receives usage telemetry for each completed agent turn. Replaces VAIS2's
/// <c>ITokenUsageTracker</c> with a stack-neutral surface that consumers (OTel,
/// Langfuse, a Postgres sink in VAIS2, a no-op) can implement independently.
/// </summary>
/// <remarks>
/// <para>
/// The core calls <see cref="ReportAsync"/> exactly once per turn, regardless of
/// success. Implementations MUST NOT throw — they fail silently or log internally.
/// Token accounting is too important to break the main flow over.
/// </para>
/// <para>
/// Implementations must be thread-safe; multiple agents may report concurrently.
/// </para>
/// </remarks>
public interface IUsageSink
{
    /// <summary>
    /// Asynchronously record a usage report. The returned task should complete
    /// quickly — sinks that do heavy work (DB writes, HTTP) must queue internally
    /// and return immediately.
    /// </summary>
    ValueTask ReportAsync(UsageRecord record, CancellationToken cancellationToken = default);
}
