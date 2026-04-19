// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;

namespace Vais.Agents.Protocols.A2A.Server;

/// <summary>
/// Configuration for <see cref="A2AAgentServerBuilder"/> + <c>MapA2AAgentServer</c>.
/// Controls AgentCard derivation, the URL prefix the endpoints mount under, and the
/// filters applied when walking the registry.
/// </summary>
/// <remarks>
/// All properties are <c>get; set;</c> to keep the configure-action idiom
/// (<c>AddA2AAgentServer(o => { o.BasePath = "/a2a"; })</c>) working — the v0.7
/// MCP server shipped init-only properties and immediately broke on that idiom.
/// </remarks>
public sealed class A2AAgentServerOptions
{
    /// <summary>
    /// Server name advertised in log lines. Default <c>"Vais.Agents A2A Server"</c>.
    /// (A2A has no <c>initialize</c>-handshake equivalent; the name is only a
    /// Vais-side identity tag — not sent on the wire.)
    /// </summary>
    public string Name { get; set; } = "Vais.Agents A2A Server";

    /// <summary>Server version tag. Default <c>"0.8"</c>. Not sent on the wire; see <see cref="Name"/> remarks.</summary>
    public string Version { get; set; } = "0.8";

    /// <summary>
    /// Base path that agent endpoints mount under. Each registered agent becomes a
    /// route at <c>{BasePath}/{id}</c> with a discovery card at
    /// <c>{BasePath}/{id}/.well-known/agent-card.json</c>. Default <c>"/agents"</c>.
    /// </summary>
    public string BasePath { get; set; } = "/agents";

    /// <summary>
    /// Optional label-prefix filter applied to <see cref="IAgentRegistry.ListAsync"/>
    /// when discovering agents to publish. Null = publish every registered agent.
    /// </summary>
    public string? LabelPrefixFilter { get; set; }

    /// <summary>
    /// Organization name written into <see cref="AgentCard.Provider"/>.
    /// Default <c>"vais-agents"</c>. Override when you need the card to surface your
    /// company name to A2A directory services.
    /// </summary>
    public string ProviderOrganization { get; set; } = "vais-agents";

    /// <summary>
    /// Post-process hook applied to the auto-derived <see cref="AgentCard"/> after
    /// <see cref="AgentCardBuilder"/> fills defaults. Tweak tags, swap the description,
    /// append custom skills here. Applied after <see cref="BuildCard"/>, before
    /// <see cref="PerAgentOverrides"/>.
    /// </summary>
    public Action<AgentManifest, AgentCard>? CustomizeCard { get; set; }

    /// <summary>
    /// Full replacement for the auto-default card. When non-null, the auto-derivation
    /// is skipped for that manifest — consumers get the raw <see cref="AgentCard"/>
    /// back from this delegate. Useful when you have a curated card you want to ship
    /// verbatim. <see cref="CustomizeCard"/> still runs afterwards if set.
    /// </summary>
    public Func<AgentManifest, AgentCard>? BuildCard { get; set; }

    /// <summary>
    /// Per-agent card overrides keyed by <see cref="AgentManifest.Id"/>. When an id
    /// is present here the lookup short-circuits both <see cref="BuildCard"/> and
    /// the auto-default — <see cref="CustomizeCard"/> does NOT run. Pairs with
    /// external card editors that hand-author cards for specific agents.
    /// </summary>
    public IReadOnlyDictionary<string, AgentCard>? PerAgentOverrides { get; set; }
}
