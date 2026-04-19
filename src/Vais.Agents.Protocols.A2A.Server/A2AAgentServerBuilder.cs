// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Protocols.A2A.Server;

/// <summary>
/// Builds per-agent <see cref="A2AServer"/> + <see cref="AgentCard"/> pairs from an
/// <see cref="IAgentRegistry"/>. Mirrors <c>McpAgentServerBuilder</c> from the v0.7 MCP
/// inbound pillar: one projection per registered agent, discovered at build time.
/// </summary>
/// <remarks>
/// Unlike MCP — where the SDK wraps a single server around a dynamic handler list — A2A
/// maps one endpoint = one agent = one card. So discovery happens eagerly here: we enumerate
/// the registry once, build a handler + card per manifest, and return the list. Consumers
/// (the <c>MapA2AAgentServer</c> extension) walk the list to mount routes.
/// </remarks>
public static class A2AAgentServerBuilder
{
    /// <summary>Build one <see cref="A2AAgentServerEntry"/> per agent in <paramref name="registry"/>.</summary>
    /// <param name="registry">The agent registry to enumerate.</param>
    /// <param name="lifecycle">Lifecycle manager that handles invocation requests.</param>
    /// <param name="baseUrl">Absolute base URL the host is serving on (<c>https://host:port</c>), used to fill <see cref="AgentInterface.Url"/> on each card.</param>
    /// <param name="options">Server options controlling card derivation + filtering. Null uses defaults.</param>
    /// <param name="taskStore">Task store backing the <see cref="A2AServer"/>. Null uses <see cref="InMemoryTaskStore"/>.</param>
    /// <param name="loggerFactory">Logger factory forwarded to each <see cref="A2AServer"/>. Null uses <see cref="NullLoggerFactory.Instance"/>.</param>
    /// <param name="cancellationToken">Cancels the registry enumeration.</param>
    public static async Task<IReadOnlyList<A2AAgentServerEntry>> BuildAsync(
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        string baseUrl,
        A2AAgentServerOptions? options = null,
        ITaskStore? taskStore = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        options ??= new A2AAgentServerOptions();
        taskStore ??= new InMemoryTaskStore();
        loggerFactory ??= NullLoggerFactory.Instance;

        var entries = new List<A2AAgentServerEntry>();
        await foreach (var manifest in registry.ListAsync(options.LabelPrefixFilter, cancellationToken).ConfigureAwait(false))
        {
            var route = BuildAgentRoute(options.BasePath, manifest.Id);
            var agentUrl = CombineUrl(baseUrl, route);
            var card = AgentCardBuilder.Build(manifest, options, agentUrl);
            var handle = new AgentHandle(manifest.Id, manifest.Version);
            var handler = new A2AAgentHandler(lifecycle, handle);
            var server = new A2AServer(
                handler: handler,
                taskStore: taskStore,
                notifier: new ChannelEventNotifier(),
                logger: loggerFactory.CreateLogger<A2AServer>(),
                options: new A2AServerOptions());
            entries.Add(new A2AAgentServerEntry(manifest.Id, manifest.Version, route, card, server));
        }
        return entries;
    }

    /// <summary>Route path (<c>{basePath}/{id}</c>) that an agent's JSON-RPC endpoint mounts at.</summary>
    internal static string BuildAgentRoute(string basePath, string agentId)
    {
        var trimmed = basePath.TrimEnd('/');
        return string.IsNullOrEmpty(trimmed)
            ? "/" + agentId
            : trimmed + "/" + agentId;
    }

    /// <summary>Combine <paramref name="baseUrl"/> with <paramref name="route"/> into an absolute URL string.</summary>
    internal static string CombineUrl(string baseUrl, string route)
    {
        var trimmedBase = baseUrl.TrimEnd('/');
        var rooted = route.StartsWith('/') ? route : "/" + route;
        return trimmedBase + rooted;
    }
}

/// <summary>One agent's projected A2A surface. Produced by <see cref="A2AAgentServerBuilder.BuildAsync"/>.</summary>
/// <param name="AgentId">Manifest id.</param>
/// <param name="AgentVersion">Manifest version.</param>
/// <param name="Route">Route path the JSON-RPC endpoint is served at — relative to the host root.</param>
/// <param name="Card">Derived (or overridden) agent card served at <c>{Route}/.well-known/agent-card.json</c>.</param>
/// <param name="Server">The A2A server wrapping the agent's handler + task store.</param>
public sealed record A2AAgentServerEntry(
    string AgentId,
    string AgentVersion,
    string Route,
    AgentCard Card,
    A2AServer Server);
