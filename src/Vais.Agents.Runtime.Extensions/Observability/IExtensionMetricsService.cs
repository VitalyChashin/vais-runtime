// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Per-handler invocation metrics aggregated over a short rolling window.
/// </summary>
public sealed record HandlerMetrics(
    string HandlerId,
    string Seam,
    double P50Seconds,
    double P95Seconds,
    double ErrorRate,
    int TotalInvocations);

/// <summary>
/// Metrics for all handlers of one extension within the rolling window.
/// </summary>
public sealed record ExtensionHandlerMetrics(
    string ExtensionId,
    IReadOnlyList<HandlerMetrics> Handlers);

/// <summary>
/// Provides per-handler invocation metrics for loaded extensions.
/// Backed by an in-process rolling window; returns <see langword="null"/> when no
/// samples have been recorded for the requested extension id.
/// </summary>
public interface IExtensionMetricsService
{
    /// <summary>
    /// Get aggregated metrics for the named extension, or <see langword="null"/> if
    /// the extension has no recorded samples in the current window.
    /// </summary>
    ExtensionHandlerMetrics? GetMetrics(string extensionId);
}
