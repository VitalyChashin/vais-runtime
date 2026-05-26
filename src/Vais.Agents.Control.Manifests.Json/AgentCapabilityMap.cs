// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// One sub-agent's worth of capability metadata, surfaced to a coordinator agent so it can
/// pick the right delegate. The capability map is built per coordinator from its manifest's
/// <see cref="AgentManifest.LocalAgents"/> cross-joined with the <c>agent:</c>-sourced
/// entries in <see cref="AgentManifest.Tools"/>.
/// </summary>
/// <param name="ToolName">The LLM-visible tool name the coordinator calls to delegate (from <see cref="ToolRef.Name"/>).</param>
/// <param name="AgentId">The effective id of the delegated agent (<see cref="LocalAgentRef.AgentId"/> or its <see cref="LocalAgentRef.Name"/>).</param>
/// <param name="Description">Effective description — local-ref override wins, else the target manifest's description, else <c>null</c>.</param>
/// <param name="Tags">Flattened <see cref="AgentManifest.Labels"/> as <c>key:value</c> strings (deployer-set capability / risk markers).</param>
/// <param name="Mode">Whether this delegation is blocking or background, surfaced so the coordinator can plan accordingly.</param>
public sealed record SubAgentCapability(
    string ToolName,
    string AgentId,
    string? Description,
    IReadOnlyList<string> Tags,
    LocalAgentInvocationMode Mode);

/// <summary>
/// All sub-agents available to one coordinator, in registration order. Built by
/// <see cref="IAgentCapabilityMapBuilder"/> and consumed by the Plan C2-2 input middleware
/// that injects it into the coordinator's <see cref="AgentInputContext"/>.
/// </summary>
/// <param name="CoordinatorAgentId">Id of the coordinator whose sub-agents this map describes.</param>
/// <param name="SubAgents">One entry per delegate-able sub-agent.</param>
public sealed record CapabilityMap(string CoordinatorAgentId, IReadOnlyList<SubAgentCapability> SubAgents)
{
    /// <summary>An empty map (coordinator has no sub-agents). Useful as a defensive default.</summary>
    public static CapabilityMap Empty(string coordinatorAgentId) => new(coordinatorAgentId, []);

    /// <summary>
    /// Render the map as a compact human-readable block suitable for in-band injection into the
    /// coordinator's system prompt or user message. Each sub-agent gets a single line:
    /// <c>- toolName: description [tags...] (mode)</c>. Returns the empty string when there are
    /// no sub-agents so callers can append unconditionally.
    /// </summary>
    public string ToCompactText()
    {
        if (SubAgents.Count == 0) return string.Empty;
        var sb = new StringBuilder(64 + SubAgents.Count * 96);
        sb.Append("Your team (delegate by calling the tool by name):\n");
        foreach (var sub in SubAgents)
        {
            sb.Append("- ").Append(sub.ToolName);
            if (!string.IsNullOrWhiteSpace(sub.Description))
                sb.Append(": ").Append(sub.Description!.Trim());
            if (sub.Tags.Count > 0)
                sb.Append(" [").Append(string.Join(", ", sub.Tags)).Append(']');
            if (sub.Mode != LocalAgentInvocationMode.Blocking)
                sb.Append(" (").Append(sub.Mode.ToString().ToLowerInvariant()).Append(')');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Builds a <see cref="CapabilityMap"/> for a coordinator agent by walking its registered
/// manifest's sub-agent bindings and cross-joining with the LLM-visible tool names. Caches
/// per coordinator id; the runtime's <c>IAgentManifestInvalidator</c> hooks already drop
/// translator-cache entries on manifest change — wire a parallel invalidation through
/// <see cref="Invalidate"/> so the capability map stays in sync.
/// </summary>
public interface IAgentCapabilityMapBuilder
{
    /// <summary>Build (or return cached) the capability map for <paramref name="coordinatorAgentId"/>.</summary>
    ValueTask<CapabilityMap> BuildAsync(string coordinatorAgentId, CancellationToken cancellationToken = default);

    /// <summary>Drop the cached map for <paramref name="coordinatorAgentId"/>. Idempotent.</summary>
    void Invalidate(string coordinatorAgentId);
}

/// <summary>
/// Default <see cref="IAgentCapabilityMapBuilder"/> over <see cref="IAgentRegistry"/>. Reads
/// each coordinator's manifest from the registry, walks its <see cref="AgentManifest.Tools"/>
/// for entries sourced as <c>agent:&lt;LocalAgentRef.Name&gt;</c>, resolves the target agent's
/// manifest (for description + labels), and produces a stable map. In-memory cache keyed by
/// coordinator id; concurrent <see cref="BuildAsync"/> calls converge on a single entry.
/// </summary>
public sealed class AgentCapabilityMapBuilder(IAgentRegistry agents) : IAgentCapabilityMapBuilder
{
    private const string AgentSourcePrefix = "agent:";
    private readonly IAgentRegistry _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly ConcurrentDictionary<string, CapabilityMap> _cache = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async ValueTask<CapabilityMap> BuildAsync(string coordinatorAgentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorAgentId);
        if (_cache.TryGetValue(coordinatorAgentId, out var cached)) return cached;

        var coordinator = await _agents.GetAsync(coordinatorAgentId, version: null, cancellationToken).ConfigureAwait(false);
        if (coordinator?.LocalAgents is not { Count: > 0 } locals)
        {
            var empty = CapabilityMap.Empty(coordinatorAgentId);
            _cache.TryAdd(coordinatorAgentId, empty);
            return empty;
        }

        var localByName = new Dictionary<string, LocalAgentRef>(locals.Count, StringComparer.Ordinal);
        foreach (var l in locals) localByName[l.Name] = l;

        var subs = new List<SubAgentCapability>();
        foreach (var toolRef in coordinator.Tools ?? [])
        {
            if (toolRef.Source is not { } src || !src.StartsWith(AgentSourcePrefix, StringComparison.Ordinal)) continue;
            var localName = src[AgentSourcePrefix.Length..];
            if (!localByName.TryGetValue(localName, out var localRef)) continue;

            var effectiveId = localRef.AgentId ?? localRef.Name;
            var target = await _agents.GetAsync(effectiveId, localRef.AgentVersion, cancellationToken).ConfigureAwait(false);
            var description = localRef.Description ?? target?.Description;
            var tags = ExtractTags(target);
            subs.Add(new SubAgentCapability(toolRef.Name, effectiveId, description, tags, localRef.Mode));
        }

        var map = new CapabilityMap(coordinatorAgentId, subs);
        _cache.TryAdd(coordinatorAgentId, map);
        return map;
    }

    /// <inheritdoc />
    public void Invalidate(string coordinatorAgentId)
    {
        if (string.IsNullOrWhiteSpace(coordinatorAgentId)) return;
        _cache.TryRemove(coordinatorAgentId, out _);
    }

    private static IReadOnlyList<string> ExtractTags(AgentManifest? manifest)
    {
        if (manifest?.Labels is not { Count: > 0 } labels) return [];
        var tags = new List<string>(labels.Count);
        foreach (var (k, v) in labels) tags.Add($"{k}:{v}");
        return tags;
    }
}
