// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Globalization;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Parses and clamps K8s-style resource quantity strings for container plugin manifests.
/// </summary>
internal static class ContainerPluginResourceParser
{
    /// <summary>
    /// Converts a K8s memory quantity string ("256Mi", "1Gi", "512Ki", plain bytes) to bytes,
    /// or null if <paramref name="raw"/> is null.
    /// </summary>
    internal static long? ParseMemoryBytes(string? raw)
    {
        if (raw is null) return null;
        raw = raw.Trim();
        if (raw.EndsWith("Gi", StringComparison.OrdinalIgnoreCase))
            return (long)(Parse(raw[..^2]) * 1024L * 1024 * 1024);
        if (raw.EndsWith("Mi", StringComparison.OrdinalIgnoreCase))
            return (long)(Parse(raw[..^2]) * 1024L * 1024);
        if (raw.EndsWith("Ki", StringComparison.OrdinalIgnoreCase))
            return (long)(Parse(raw[..^2]) * 1024L);
        if (raw.EndsWith("G", StringComparison.OrdinalIgnoreCase))
            return (long)(Parse(raw[..^1]) * 1_000_000_000L);
        if (raw.EndsWith("M", StringComparison.OrdinalIgnoreCase))
            return (long)(Parse(raw[..^1]) * 1_000_000L);
        if (raw.EndsWith("K", StringComparison.OrdinalIgnoreCase))
            return (long)(Parse(raw[..^1]) * 1_000L);
        return (long)Parse(raw);
    }

    /// <summary>
    /// Converts a K8s CPU quantity string ("0.5", "500m") to nanoCPUs (1 CPU = 1_000_000_000),
    /// or null if <paramref name="raw"/> is null.
    /// </summary>
    internal static long? ParseNanoCpus(string? raw)
    {
        if (raw is null) return null;
        raw = raw.Trim();
        if (raw.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            return (long)(Parse(raw[..^1]) * 1_000_000L);
        return (long)(Parse(raw) * 1_000_000_000L);
    }

    /// <summary>
    /// Returns the smaller of <paramref name="value"/> and <paramref name="max"/>,
    /// or null if <paramref name="value"/> is null.
    /// </summary>
    internal static long? Clamp(long? value, long max) =>
        value.HasValue ? Math.Min(value.Value, max) : null;

    private static double Parse(string s) =>
        double.Parse(s, CultureInfo.InvariantCulture);
}
