// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Gateways.McpGovernance;

/// <summary>
/// Defines which tools are permitted in a workspace, with optional prefix-based allow/deny lists
/// and a minimum privilege level requirement.
/// </summary>
/// <param name="AllowedPrefixes">
/// Tool name prefixes that are permitted. When empty, all tools not in <see cref="DeniedPrefixes"/> are allowed.
/// </param>
/// <param name="DeniedPrefixes">Tool name prefixes that are always denied, regardless of <see cref="AllowedPrefixes"/>.</param>
/// <param name="MinPrivilegeLevel">
/// Minimum <see cref="PrivilegeLevel"/> (as int) required. 0 = Platform (highest privilege), 2 = Agent (lowest).
/// Default: 0 (no privilege restriction).
/// </param>
public sealed record WorkspaceToolPolicy(
    IReadOnlyList<string> AllowedPrefixes,
    IReadOnlyList<string> DeniedPrefixes,
    int MinPrivilegeLevel = 0)
{
    /// <summary>
    /// Returns <see langword="true"/> if the tool call is permitted for the given caller privilege level.
    /// </summary>
    public bool IsAllowed(string toolName, PrivilegeLevel? callerLevel)
    {
        var level = (int)(callerLevel ?? PrivilegeLevel.Platform);
        if (level > MinPrivilegeLevel) return false;

        foreach (var d in DeniedPrefixes)
            if (toolName.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return false;

        if (AllowedPrefixes.Count == 0) return true;

        foreach (var a in AllowedPrefixes)
            if (toolName.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
