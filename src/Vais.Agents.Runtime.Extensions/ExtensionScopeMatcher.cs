// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Evaluates whether an <see cref="ExtensionScope"/> matches a given agent.
/// </summary>
/// <remarks>
/// Scope matching rules (per OQ-7):
/// <list type="bullet">
///   <item>Null scope = cluster-wide: always matches.</item>
///   <item><c>AgentIds</c> non-empty = <c>agentId</c> must appear in the list.</item>
///   <item><c>Workspaces</c> non-empty = <c>manifest.Labels["workspace"]</c> must appear in the list.
///   When the manifest is unavailable, workspace filtering is skipped (cluster-wide fallback).</item>
///   <item><c>Selector</c> non-null = every key in <c>Selector.MatchLabels</c> must match a manifest label.
///   When the manifest is unavailable, selector filtering is skipped.</item>
///   <item>Multiple scope fields AND together; values within each field OR together.</item>
/// </list>
/// </remarks>
internal static class ExtensionScopeMatcher
{
    private const string WorkspaceLabelKey = "workspace";

    /// <summary>
    /// Match against a full manifest. Use when the manifest is available.
    /// </summary>
    public static bool Matches(ExtensionScope? scope, AgentManifest agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return MatchesCore(scope, agent, agent.Id);
    }

    /// <summary>
    /// Match against an optional manifest and an agent id.
    /// When <paramref name="manifest"/> is null, AgentIds filtering still works;
    /// Workspaces and Selector filtering are skipped (conservative: match assumed).
    /// </summary>
    public static bool Matches(ExtensionScope? scope, AgentManifest? manifest, string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return MatchesCore(scope, manifest, agentId);
    }

    private static bool MatchesCore(ExtensionScope? scope, AgentManifest? manifest, string agentId)
    {
        if (scope is null)
        {
            return true; // cluster-wide
        }

        if (scope.AgentIds is { Count: > 0 })
        {
            if (!scope.AgentIds.Contains(agentId, StringComparer.Ordinal))
            {
                return false;
            }
        }

        if (scope.Workspaces is { Count: > 0 } && manifest is not null)
        {
            var agentWorkspace = manifest.Labels is not null && manifest.Labels.TryGetValue(WorkspaceLabelKey, out var ws)
                ? ws
                : null;

            if (agentWorkspace is null || !scope.Workspaces.Contains(agentWorkspace, StringComparer.Ordinal))
            {
                return false;
            }
        }

        if (scope.Selector is not null && manifest is not null)
        {
            foreach (var (key, value) in scope.Selector.MatchLabels)
            {
                if (manifest.Labels is null ||
                    !manifest.Labels.TryGetValue(key, out var agentValue) ||
                    !string.Equals(agentValue, value, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
