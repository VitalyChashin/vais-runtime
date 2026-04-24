// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Extracts the <c>[tool.vais.plugin]</c> section from a <c>pyproject.toml</c> file.
/// Only reads the two fields the Python plugin loader requires: <c>targetApiVersion</c>
/// and <c>tools</c>. All other pyproject.toml content is ignored.
/// </summary>
/// <remarks>
/// Uses a lightweight line-by-line state machine rather than a full TOML parser to
/// avoid introducing a third-party TOML dependency on the hot-path where only two
/// scalar values from one known section are needed.
/// </remarks>
internal sealed class PyprojectTomlReader
{
    /// <summary>
    /// Parses <paramref name="tomlContent"/> and returns the Vais plugin section, or
    /// <see langword="null"/> when <c>[tool.vais.plugin]</c> is absent or
    /// <c>targetApiVersion</c> is missing from it.
    /// Throws <see cref="FormatException"/> on malformed array syntax.
    /// </summary>
    internal PyprojectTomlSection? Read(string tomlContent)
    {
        var lines = tomlContent.Split('\n');
        var inSection = false;
        string? targetApiVersion = null;
        List<string>? tools = null;
        var arrayBuffer = new System.Text.StringBuilder();
        var insideMultilineArray = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Accumulate multi-line array values until the closing bracket.
            if (insideMultilineArray)
            {
                arrayBuffer.Append(' ').Append(line.Trim());
                if (line.Contains(']'))
                {
                    tools = ParseStringArray(arrayBuffer.ToString());
                    insideMultilineArray = false;
                }
                continue;
            }

            var trimmed = line.Trim();

            // Skip blank lines and comments.
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            // Section header — detect section changes.
            if (trimmed[0] == '[')
            {
                // Strip one or two leading/trailing brackets (handles both [x] and [[x]]).
                var sectionName = trimmed.TrimStart('[').TrimEnd(']').Trim();
                inSection = string.Equals(sectionName, "tool.vais.plugin", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
                continue;

            // Key = value pair inside the target section.
            var eqIdx = trimmed.IndexOf('=', StringComparison.Ordinal);
            if (eqIdx < 0)
                continue;

            var key = trimmed[..eqIdx].Trim();
            var value = trimmed[(eqIdx + 1)..].Trim();

            if (string.Equals(key, "targetApiVersion", StringComparison.OrdinalIgnoreCase))
            {
                targetApiVersion = UnquoteTomlString(value);
            }
            else if (string.Equals(key, "tools", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Contains(']'))
                {
                    tools = ParseStringArray(value);
                }
                else
                {
                    // Multi-line array: accumulate until the closing bracket.
                    arrayBuffer.Clear();
                    arrayBuffer.Append(value);
                    insideMultilineArray = true;
                }
            }
        }

        if (insideMultilineArray)
            throw new FormatException($"Unclosed array for 'tools' in [tool.vais.plugin]: {arrayBuffer}");

        if (targetApiVersion is null)
            return null;

        return new PyprojectTomlSection(targetApiVersion, tools ?? []);
    }

    private static string UnquoteTomlString(string s)
    {
        s = s.Trim();
        if (s.Length >= 2)
        {
            if (s[0] == '"' && s[^1] == '"')
                return s[1..^1];
            if (s[0] == '\'' && s[^1] == '\'')
                return s[1..^1];
        }
        return s;
    }

    private static List<string> ParseStringArray(string s)
    {
        s = s.Trim();
        if (!s.StartsWith('[') || !s.Contains(']'))
            throw new FormatException($"Expected a TOML string array starting with '[', got: {s}");

        var closeIdx = s.LastIndexOf(']');
        var inner = s[1..closeIdx];

        var result = new List<string>();
        foreach (var item in inner.Split(','))
        {
            var unquoted = UnquoteTomlString(item.Trim());
            if (unquoted.Length > 0)
                result.Add(unquoted);
        }
        return result;
    }
}

/// <summary>Parsed content of the <c>[tool.vais.plugin]</c> TOML section.</summary>
internal sealed record PyprojectTomlSection(
    string TargetApiVersion,
    IReadOnlyList<string> Tools);
