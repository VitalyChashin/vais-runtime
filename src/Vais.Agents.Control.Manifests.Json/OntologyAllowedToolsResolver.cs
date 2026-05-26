// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Plan C2-4 — computes the set of sub-agent tool names a coordinator's caller is allowed
/// to delegate to, based on the caller's scopes (typically from <see cref="AgentContext.Scopes"/>
/// derived from the OAuth bearer) intersected with each sub-agent's capability tags from the
/// <see cref="CapabilityMap"/>. The deployer pipes this resolver's output into
/// <see cref="AgentContext.AllowedTools"/>; the existing tool dispatcher then enforces it
/// at the <c>DefaultToolCallDispatcher.cs:157</c> path — no new enforcement code.
/// </summary>
public interface IOntologyAllowedToolsResolver
{
    /// <summary>
    /// Return the set of <see cref="SubAgentCapability.ToolName"/>s in <paramref name="map"/>
    /// the caller is allowed to invoke given <paramref name="callerScopes"/>.
    /// </summary>
    IReadOnlySet<string> Compute(IReadOnlyList<string>? callerScopes, CapabilityMap map);
}

/// <summary>Configuration for <see cref="OntologyAllowedToolsResolver"/>.</summary>
public sealed record OntologyAllowedToolsResolverOptions
{
    /// <summary>
    /// When <see langword="true"/> (default) an empty / null caller-scope list grants access
    /// to every sub-agent — the dev / single-tenant posture, mirroring how
    /// <see cref="AgentContext.AllowedTools"/> being <see langword="null"/> means "all tools
    /// allowed". Set to <see langword="false"/> in multi-tenant deployments where an
    /// unauthenticated caller must see *no* sub-agents.
    /// </summary>
    public bool GrantOnEmptyScope { get; init; } = true;

    /// <summary>
    /// Wildcard scope value that grants access to every tagged sub-agent. Defaults to
    /// <c>"*"</c>. Set to an empty / null to disable wildcard handling.
    /// </summary>
    public string? WildcardScope { get; init; } = "*";
}

/// <summary>
/// Default <see cref="IOntologyAllowedToolsResolver"/>. Tag-intersection policy:
/// <list type="bullet">
///   <item><description>A sub-agent with no tags is always allowed (open by default — deployers tag the sub-agents they want to restrict).</description></item>
///   <item><description>A sub-agent with tags is allowed iff at least one of its tags equals a caller scope (case-sensitive string match), or the caller carries the wildcard scope.</description></item>
///   <item><description>An empty / null caller-scope list is treated per <see cref="OntologyAllowedToolsResolverOptions.GrantOnEmptyScope"/>.</description></item>
/// </list>
/// </summary>
public sealed class OntologyAllowedToolsResolver(OntologyAllowedToolsResolverOptions? options = null) : IOntologyAllowedToolsResolver
{
    private readonly OntologyAllowedToolsResolverOptions _options = options ?? new();

    /// <inheritdoc />
    public IReadOnlySet<string> Compute(IReadOnlyList<string>? callerScopes, CapabilityMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        var scopes = callerScopes ?? [];
        var emptyScopes = scopes.Count == 0;
        var wildcard = _options.WildcardScope is { Length: > 0 } w ? w : null;
        var wildcardPresent = wildcard is not null && scopes.Contains(wildcard);

        foreach (var sub in map.SubAgents)
        {
            // Untagged sub-agents = open. The deployer must tag them to restrict.
            if (sub.Tags.Count == 0)
            {
                allowed.Add(sub.ToolName);
                continue;
            }

            if (wildcardPresent)
            {
                allowed.Add(sub.ToolName);
                continue;
            }

            if (emptyScopes)
            {
                if (_options.GrantOnEmptyScope) allowed.Add(sub.ToolName);
                continue;
            }

            foreach (var tag in sub.Tags)
            {
                if (ScopeIntersects(scopes, tag))
                {
                    allowed.Add(sub.ToolName);
                    break;
                }
            }
        }
        return allowed;
    }

    private static bool ScopeIntersects(IReadOnlyList<string> scopes, string tag)
    {
        for (var i = 0; i < scopes.Count; i++)
            if (string.Equals(scopes[i], tag, StringComparison.Ordinal)) return true;
        return false;
    }
}
