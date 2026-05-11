// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// <see cref="IMcpServerConnectionChangedHook"/> that invalidates the manifest-translator
/// cache for every agent that references the reconnected physical MCP server via
/// <c>transport:registered</c>. Registered automatically by
/// <see cref="AgentManifestInstantiatorServiceCollectionExtensions.AddAgentManifestInstantiator"/>.
/// </summary>
internal sealed class McpTranslatorInvalidationHook : IMcpServerConnectionChangedHook
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentManifestTranslator _translator;
    private readonly ILogger<McpTranslatorInvalidationHook> _logger;

    internal McpTranslatorInvalidationHook(
        IAgentRegistry registry,
        IAgentManifestTranslator translator,
        ILogger<McpTranslatorInvalidationHook>? logger = null)
    {
        _registry = registry;
        _translator = translator;
        _logger = logger ?? NullLogger<McpTranslatorInvalidationHook>.Instance;
    }

    public Task OnConnectedAsync(string serverId, CancellationToken cancellationToken = default)
        => InvalidateAffectedAgentsAsync(serverId, cancellationToken);

    public Task OnDisconnectedAsync(string serverId, CancellationToken cancellationToken = default)
        => InvalidateAffectedAgentsAsync(serverId, cancellationToken);

    private async Task InvalidateAffectedAgentsAsync(string serverId, CancellationToken cancellationToken)
    {
        var count = 0;
        await foreach (var manifest in _registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (!ReferencesServer(manifest, serverId)) continue;

            await _translator.InvalidateAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
            count++;
            _logger.LogDebug(
                "translator-invalidated: agent '{AgentId}' (mcp-server='{ServerId}')",
                manifest.Id, serverId);
        }

        if (count > 0)
            _logger.LogInformation(
                "translator-invalidated: {Count} agent(s) for MCP server '{ServerId}'",
                count, serverId);
    }

    private static bool ReferencesServer(AgentManifest manifest, string serverId)
        => manifest.McpServers?.Any(r =>
            r.Transport == McpServerRef.RegisteredTransport
            && r.Name == serverId) == true;
}
