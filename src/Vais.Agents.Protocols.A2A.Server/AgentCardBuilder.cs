// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;

namespace Vais.Agents.Protocols.A2A.Server;

/// <summary>
/// Auto-derives an <see cref="AgentCard"/> from a Vais <see cref="AgentManifest"/> +
/// <see cref="A2AAgentServerOptions"/>. Single default <c>invoke</c> skill per agent —
/// <see cref="AgentSkill"/> has no clean map from Vais <c>Tools</c> / <c>Handoffs</c>,
/// and inventing multiple skills would be lossy. Consumers fill real taxonomies via
/// <see cref="A2AAgentServerOptions.CustomizeCard"/> / <see cref="A2AAgentServerOptions.BuildCard"/>
/// / <see cref="A2AAgentServerOptions.PerAgentOverrides"/>.
/// </summary>
public static class AgentCardBuilder
{
    /// <summary>Skill id used on the auto-default card. Stable so consumers can override it by id.</summary>
    public const string DefaultSkillId = "invoke";

    /// <summary>Build the <see cref="AgentCard"/> for <paramref name="manifest"/> per the option-precedence rules.</summary>
    /// <remarks>
    /// Precedence:
    /// <list type="number">
    ///   <item><description><see cref="A2AAgentServerOptions.PerAgentOverrides"/> — when keyed by id, wins; hooks do NOT run.</description></item>
    ///   <item><description><see cref="A2AAgentServerOptions.BuildCard"/> — when set, its result replaces the auto-default; <see cref="A2AAgentServerOptions.CustomizeCard"/> still runs.</description></item>
    ///   <item><description>Auto-default (this type) — used when neither hook above applies.</description></item>
    ///   <item><description><see cref="A2AAgentServerOptions.CustomizeCard"/> — post-processes whatever card came out of step 2 or 3.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="manifest">Source manifest.</param>
    /// <param name="options">Server options (provider org, hooks, overrides).</param>
    /// <param name="agentUrl">Absolute URL the agent is published at. Written into <see cref="AgentInterface.Url"/>.</param>
    public static AgentCard Build(AgentManifest manifest, A2AAgentServerOptions options, string agentUrl)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(agentUrl);

        if (options.PerAgentOverrides is { } map &&
            map.TryGetValue(manifest.Id, out var overrideCard))
        {
            return overrideCard;
        }

        AgentCard card = options.BuildCard is { } builder
            ? builder(manifest)
            : BuildDefault(manifest, options, agentUrl);

        options.CustomizeCard?.Invoke(manifest, card);
        return card;
    }

    internal static AgentCard BuildDefault(AgentManifest manifest, A2AAgentServerOptions options, string agentUrl)
    {
        var description = manifest.Description ?? manifest.Id;
        var card = new AgentCard
        {
            Name = manifest.Id,
            Description = description,
            Version = manifest.Version,
            Provider = new AgentProvider
            {
                Organization = options.ProviderOrganization,
            },
            Capabilities = new AgentCapabilities
            {
                Streaming = false,
                PushNotifications = false,
            },
            DefaultInputModes = { "text" },
            DefaultOutputModes = { "text" },
            Skills =
            {
                new AgentSkill
                {
                    Id = DefaultSkillId,
                    Name = manifest.Id,
                    Description = description,
                },
            },
            SupportedInterfaces =
            {
                new AgentInterface
                {
                    Url = agentUrl,
                    ProtocolBinding = ProtocolBindingNames.JsonRpc,
                },
            },
        };

        // Labels + Annotations round-trip through the card's provider URL when present.
        if (manifest.Labels is { } labels &&
            labels.TryGetValue("a2a.provider-url", out var providerUrl) &&
            !string.IsNullOrWhiteSpace(providerUrl))
        {
            card.Provider.Url = providerUrl;
        }

        return card;
    }
}
