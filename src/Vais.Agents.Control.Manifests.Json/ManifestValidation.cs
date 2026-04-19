// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Shared validation primitives used by <see cref="JsonAgentManifestLoader"/> and
/// (via delegation) the YAML loader. Kept internal — the only surface consumers
/// see is <see cref="AgentManifestValidationException.Errors"/>.
/// </summary>
internal static class ManifestValidation
{
    private static readonly Regex IdRegex = new(@"^[a-z][a-z0-9-]{0,62}$", RegexOptions.Compiled);
    private static readonly Regex VersionRegex = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex LabelKeyRegex = new(@"^[a-z0-9][a-z0-9_.\-]{0,62}$", RegexOptions.Compiled);

    public static bool IsValidId(string value) => IdRegex.IsMatch(value);
    public static bool IsValidVersion(string value) => VersionRegex.IsMatch(value);
    public static bool IsValidLabelKey(string value) => LabelKeyRegex.IsMatch(value);

    /// <summary>
    /// Parse a Go-style duration (<c>30s</c>, <c>2m</c>, <c>1h</c>, <c>45ms</c>)
    /// or an ISO 8601 duration (<c>PT30S</c>). Returns false for any other shape.
    /// </summary>
    public static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();

        // ISO 8601 quick path.
        if (value.StartsWith("P", StringComparison.Ordinal))
        {
            try { duration = System.Xml.XmlConvert.ToTimeSpan(value); return duration > TimeSpan.Zero; }
            catch (FormatException) { return false; }
        }

        // Go-style: digits + unit (ms | s | m | h | d).
        var match = Regex.Match(value, @"^(\d+(?:\.\d+)?)(ms|s|m|h|d)$", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num)) return false;
        duration = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(num),
            "s" => TimeSpan.FromSeconds(num),
            "m" => TimeSpan.FromMinutes(num),
            "h" => TimeSpan.FromHours(num),
            "d" => TimeSpan.FromDays(num),
            _ => TimeSpan.Zero,
        };
        return duration > TimeSpan.Zero;
    }
}
