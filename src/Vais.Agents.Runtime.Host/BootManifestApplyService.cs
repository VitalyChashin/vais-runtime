// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Startup hosted service that applies manifest files from a configured directory to the
/// registry on every runtime start. Provides Docker-style "restart persistence" for
/// <c>localhost</c> mode where Orleans grain storage is in-memory.
/// </summary>
/// <remarks>
/// <para>
/// Registered only when <c>VAIS_BOOT_MANIFESTS_DIRECTORY</c> is set.
/// Files are processed in ordinal filename order. All five resource kinds are supported:
/// <c>Agent</c>, <c>AgentGraph</c>, <c>LlmGatewayConfig</c>, <c>McpGatewayConfig</c>,
/// <c>McpServer</c> — same format as <c>vais apply -f</c>.
/// </para>
/// <para>
/// <b>Error handling.</b> Parse failures and unexpected apply errors log at
/// <see cref="LogLevel.Error"/> and continue to the next file/resource so a single bad
/// manifest does not block the rest. Resources already registered at the same
/// (id, version) log at <see cref="LogLevel.Debug"/> and are skipped.
/// </para>
/// </remarks>
internal sealed class BootManifestApplyService : IHostedService
{
    private readonly string _directory;
    private readonly IAgentLifecycleManager _agents;
    private readonly IAgentGraphLifecycleManager _graphs;
    private readonly ILlmGatewayConfigLifecycleManager _llmConfigs;
    private readonly IMcpGatewayConfigLifecycleManager _mcpConfigs;
    private readonly IMcpServerLifecycleManager _mcpServers;
    private readonly ILogger<BootManifestApplyService> _logger;

    public BootManifestApplyService(
        string directory,
        IAgentLifecycleManager agents,
        IAgentGraphLifecycleManager graphs,
        ILlmGatewayConfigLifecycleManager llmConfigs,
        IMcpGatewayConfigLifecycleManager mcpConfigs,
        IMcpServerLifecycleManager mcpServers,
        ILogger<BootManifestApplyService> logger)
    {
        _directory = directory;
        _agents = agents;
        _graphs = graphs;
        _llmConfigs = llmConfigs;
        _mcpConfigs = mcpConfigs;
        _mcpServers = mcpServers;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            _logger.LogWarning("Boot-apply: directory '{Directory}' does not exist — skipping.", _directory);
            return;
        }

        var files = Directory.GetFiles(_directory, "*", SearchOption.TopDirectoryOnly)
            .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        if (files.Length == 0)
        {
            _logger.LogInformation("Boot-apply: directory '{Directory}' contains no manifest files.", _directory);
            return;
        }

        _logger.LogInformation("Boot-apply: processing {Count} file(s) from '{Directory}'.", files.Length, _directory);

        var applied = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var file in files)
        {
            IReadOnlyList<ManifestResource> resources;
            try
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var isJson = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                resources = isJson
                    ? await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(content, cancellationToken).ConfigureAwait(false)
                    : await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(content, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Boot-apply: failed to parse '{File}' — skipping.", file);
                failed++;
                continue;
            }

            foreach (var resource in resources)
            {
                try
                {
                    switch (resource)
                    {
                        case ManifestResource.AgentCase { Manifest: var m }:
                            await _agents.CreateAsync(m, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Boot-apply: agent '{Id}' v{Version} applied.", m.Id, m.Version);
                            applied++;
                            break;

                        case ManifestResource.AgentGraphCase { Graph: var g }:
                            await _graphs.CreateAsync(g, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Boot-apply: graph '{Id}' v{Version} applied.", g.Id, g.Version);
                            applied++;
                            break;

                        case ManifestResource.LlmGatewayConfigCase { Config: var c }:
                            await _llmConfigs.CreateAsync(c, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Boot-apply: llm-gateway '{Id}' v{Version} applied.", c.Id, c.Version);
                            applied++;
                            break;

                        case ManifestResource.McpGatewayConfigCase { Config: var c }:
                            await _mcpConfigs.CreateAsync(c, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Boot-apply: mcp-gateway '{Id}' v{Version} applied.", c.Id, c.Version);
                            applied++;
                            break;

                        case ManifestResource.McpServerCase { Server: var s }:
                            await _mcpServers.CreateAsync(s, cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Boot-apply: mcp-server '{Id}' v{Version} applied.", s.Id, s.Version);
                            applied++;
                            break;
                    }
                }
                catch (LlmGatewayConfigConflictException ex)
                {
                    _logger.LogDebug("Boot-apply: llm-gateway '{Id}' v{Version} already registered — skipping.", ex.ConfigId, ex.Version);
                    skipped++;
                }
                catch (McpGatewayConfigConflictException ex)
                {
                    _logger.LogDebug("Boot-apply: mcp-gateway '{Id}' v{Version} already registered — skipping.", ex.ConfigId, ex.Version);
                    skipped++;
                }
                catch (McpServerConflictException ex)
                {
                    _logger.LogDebug("Boot-apply: mcp-server '{Id}' v{Version} already registered — skipping.", ex.ServerId, ex.Version);
                    skipped++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Boot-apply: failed to apply resource from '{File}' — skipping.", file);
                    failed++;
                }
            }
        }

        _logger.LogInformation(
            "Boot-apply complete: {Applied} applied, {Skipped} skipped (already registered), {Failed} failed.",
            applied, skipped, failed);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
