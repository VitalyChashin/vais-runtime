// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Control.Mcp.Server;

/// <summary>
/// Routes the <c>vais.apply</c> / <c>vais.delete</c> verbs through the per-kind
/// lifecycle managers (not the registries) so every mutation passes the same
/// RBAC + approval + audit seam the REST control plane uses. Because the
/// design-MCP endpoint is covered by the global principal-mapping middleware, the
/// managers see the caller's JWT scopes via the ambient context — no extra wiring.
/// </summary>
/// <remarks>
/// Scoped to the six kinds that have lifecycle managers. <c>EvalSuite</c> is authored
/// via <c>vais.eval</c> (inline suites); <c>Extension</c> is DLL-coupled and not
/// applyable over MCP. Both return a clear "not supported" result.
/// </remarks>
internal static class DesignMutationRouter
{
    internal static readonly IReadOnlySet<string> MutableKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "Agent", "AgentGraph", "McpServer", "LlmGatewayConfig", "McpGatewayConfig", "ContainerPlugin",
    };

    internal static async ValueTask<JsonObject> ApplyAsync(string manifestJson, IServiceProvider services, CancellationToken ct)
    {
        IReadOnlyList<ManifestResource> resources;
        try
        {
            resources = await new JsonAgentGraphManifestLoader()
                .LoadAllResourcesFromStringAsync(manifestJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentManifestValidationException or JsonException)
        {
            return Error($"manifest parse failed: {ex.Message}");
        }

        if (resources.Count != 1)
            return Error($"vais.apply accepts exactly one manifest; got {resources.Count}.");

        return resources[0] switch
        {
            ManifestResource.AgentCase a => await ApplyAgentAsync(a.Manifest, services, ct).ConfigureAwait(false),
            ManifestResource.AgentGraphCase g => await ApplyGraphAsync(g.Graph, services, ct).ConfigureAwait(false),
            ManifestResource.McpServerCase s => await ApplyMcpServerAsync(s.Server, services, ct).ConfigureAwait(false),
            ManifestResource.McpGatewayConfigCase m => await ApplyMcpGatewayAsync(m.Config, services, ct).ConfigureAwait(false),
            ManifestResource.LlmGatewayConfigCase l => await ApplyLlmGatewayAsync(l.Config, services, ct).ConfigureAwait(false),
            ManifestResource.ContainerPluginCase c => await ApplyContainerPluginAsync(c.Manifest, services, ct).ConfigureAwait(false),
            var other => Error(
                $"kind '{other.GetType().Name.Replace("Case", "")}' is not applyable over MCP. " +
                "Supported: Agent, AgentGraph, McpServer, McpGatewayConfig, LlmGatewayConfig, ContainerPlugin. " +
                "(EvalSuite: use vais.eval with an inline suite; Extension: apply via the control-plane API with its DLL.)"),
        };
    }

    internal static async ValueTask<JsonObject> DeleteAsync(string kind, string name, string? version, IServiceProvider services, CancellationToken ct)
    {
        // Resolve acronym-mis-cased kinds (e.g. "LLMGatewayConfig") to canonical before the exact-match switch.
        kind = DesignRegistryRouter.Normalize(kind) ?? kind;
        switch (kind)
        {
            case "Agent":
            {
                var reg = services.GetRequiredService<IAgentRegistry>();
                var m = await reg.GetAsync(name, version, ct).ConfigureAwait(false);
                if (m is null) return NotFound(kind, name);
                await services.GetRequiredService<IAgentLifecycleManager>()
                    .EvictAsync(new AgentHandle(m.Id, m.Version), ct).ConfigureAwait(false);
                return Deleted(kind, m.Id, m.Version);
            }
            case "AgentGraph":
            {
                var reg = services.GetRequiredService<IAgentGraphRegistry>();
                var m = await reg.GetAsync(name, version, ct).ConfigureAwait(false);
                if (m is null) return NotFound(kind, name);
                await services.GetRequiredService<IAgentGraphLifecycleManager>()
                    .EvictAsync(new AgentGraphHandle(m.Id, m.Version), ct).ConfigureAwait(false);
                return Deleted(kind, m.Id, m.Version);
            }
            case "McpServer":
            {
                var reg = services.GetRequiredService<IMcpServerRegistry>();
                var m = await reg.GetAsync(name, version, ct).ConfigureAwait(false);
                if (m is null) return NotFound(kind, name);
                await services.GetRequiredService<IMcpServerLifecycleManager>()
                    .EvictAsync(new McpServerHandle(m.Id, m.Version), ct).ConfigureAwait(false);
                return Deleted(kind, m.Id, m.Version);
            }
            case "McpGatewayConfig":
            {
                var reg = services.GetRequiredService<IMcpGatewayConfigRegistry>();
                var m = await reg.GetAsync(name, version, ct).ConfigureAwait(false);
                if (m is null) return NotFound(kind, name);
                await services.GetRequiredService<IMcpGatewayConfigLifecycleManager>()
                    .EvictAsync(new McpGatewayConfigHandle(m.Id, m.Version), ct).ConfigureAwait(false);
                return Deleted(kind, m.Id, m.Version);
            }
            case "LlmGatewayConfig":
            {
                var reg = services.GetRequiredService<ILlmGatewayConfigRegistry>();
                var m = await reg.GetAsync(name, version, ct).ConfigureAwait(false);
                if (m is null) return NotFound(kind, name);
                await services.GetRequiredService<ILlmGatewayConfigLifecycleManager>()
                    .EvictAsync(new LlmGatewayConfigHandle(m.Id, m.Version), ct).ConfigureAwait(false);
                return Deleted(kind, m.Id, m.Version);
            }
            case "ContainerPlugin":
            {
                var reg = services.GetRequiredService<IContainerPluginRegistry>();
                var m = await reg.GetAsync(name, version, ct).ConfigureAwait(false);
                if (m is null) return NotFound(kind, name);
                await services.GetRequiredService<IContainerPluginLifecycleManager>()
                    .EvictAsync(new ContainerPluginHandle(m.Id, m.Version), ct).ConfigureAwait(false);
                return Deleted(kind, m.Id, m.Version);
            }
            default:
                return Error($"kind '{kind}' is not deletable over MCP. Supported: {string.Join(", ", MutableKinds)}.");
        }
    }

    // ── Per-kind apply (create-or-update via the lifecycle manager) ────────────

    private static async ValueTask<JsonObject> ApplyAgentAsync(AgentManifest m, IServiceProvider sp, CancellationToken ct)
    {
        var mgr = sp.GetService<IAgentLifecycleManager>();
        if (mgr is null) return Error("Agent lifecycle manager is not available on this runtime.");
        var existing = await sp.GetRequiredService<IAgentRegistry>().GetAsync(m.Id, m.Version, ct).ConfigureAwait(false);
        if (existing is null) { var h = await mgr.CreateAsync(m, ct).ConfigureAwait(false); return Applied("Agent", h.AgentId, h.Version, created: true); }
        var u = await mgr.UpdateAsync(new AgentHandle(m.Id, m.Version), m, ct).ConfigureAwait(false);
        return Applied("Agent", u.AgentId, u.Version, created: false);
    }

    private static async ValueTask<JsonObject> ApplyGraphAsync(AgentGraphManifest m, IServiceProvider sp, CancellationToken ct)
    {
        var mgr = sp.GetService<IAgentGraphLifecycleManager>();
        if (mgr is null) return Error("AgentGraph lifecycle manager is not available on this runtime.");
        var existing = await sp.GetRequiredService<IAgentGraphRegistry>().GetAsync(m.Id, m.Version, ct).ConfigureAwait(false);
        if (existing is null) { var h = await mgr.CreateAsync(m, ct).ConfigureAwait(false); return Applied("AgentGraph", h.GraphId, h.Version, created: true); }
        var u = await mgr.UpdateAsync(new AgentGraphHandle(m.Id, m.Version), m, ct).ConfigureAwait(false);
        return Applied("AgentGraph", u.GraphId, u.Version, created: false);
    }

    private static async ValueTask<JsonObject> ApplyMcpServerAsync(McpServerManifest m, IServiceProvider sp, CancellationToken ct)
    {
        var mgr = sp.GetService<IMcpServerLifecycleManager>();
        if (mgr is null) return Error("McpServer lifecycle manager is not available on this runtime.");
        var existing = await sp.GetRequiredService<IMcpServerRegistry>().GetAsync(m.Id, m.Version, ct).ConfigureAwait(false);
        if (existing is null) { var h = await mgr.CreateAsync(m, ct).ConfigureAwait(false); return Applied("McpServer", h.Id, h.Version, created: true); }
        var u = await mgr.UpdateAsync(new McpServerHandle(m.Id, m.Version), m, ct).ConfigureAwait(false);
        return Applied("McpServer", u.Id, u.Version, created: false);
    }

    private static async ValueTask<JsonObject> ApplyMcpGatewayAsync(McpGatewayConfigManifest m, IServiceProvider sp, CancellationToken ct)
    {
        var mgr = sp.GetService<IMcpGatewayConfigLifecycleManager>();
        if (mgr is null) return Error("McpGatewayConfig lifecycle manager is not available on this runtime.");
        var existing = await sp.GetRequiredService<IMcpGatewayConfigRegistry>().GetAsync(m.Id, m.Version, ct).ConfigureAwait(false);
        if (existing is null) { var h = await mgr.CreateAsync(m, ct).ConfigureAwait(false); return Applied("McpGatewayConfig", h.Id, h.Version, created: true); }
        var u = await mgr.UpdateAsync(new McpGatewayConfigHandle(m.Id, m.Version), m, ct).ConfigureAwait(false);
        return Applied("McpGatewayConfig", u.Id, u.Version, created: false);
    }

    private static async ValueTask<JsonObject> ApplyLlmGatewayAsync(LlmGatewayConfigManifest m, IServiceProvider sp, CancellationToken ct)
    {
        var mgr = sp.GetService<ILlmGatewayConfigLifecycleManager>();
        if (mgr is null) return Error("LlmGatewayConfig lifecycle manager is not available on this runtime.");
        var existing = await sp.GetRequiredService<ILlmGatewayConfigRegistry>().GetAsync(m.Id, m.Version, ct).ConfigureAwait(false);
        if (existing is null) { var h = await mgr.CreateAsync(m, ct).ConfigureAwait(false); return Applied("LlmGatewayConfig", h.Id, h.Version, created: true); }
        var u = await mgr.UpdateAsync(new LlmGatewayConfigHandle(m.Id, m.Version), m, ct).ConfigureAwait(false);
        return Applied("LlmGatewayConfig", u.Id, u.Version, created: false);
    }

    private static async ValueTask<JsonObject> ApplyContainerPluginAsync(ContainerPluginManifest m, IServiceProvider sp, CancellationToken ct)
    {
        var mgr = sp.GetService<IContainerPluginLifecycleManager>();
        if (mgr is null) return Error("ContainerPlugin lifecycle manager is not available on this runtime.");
        var existing = await sp.GetRequiredService<IContainerPluginRegistry>().GetAsync(m.Id, m.Version, ct).ConfigureAwait(false);
        if (existing is null) { var h = await mgr.CreateAsync(m, ct).ConfigureAwait(false); return Applied("ContainerPlugin", h.Id, h.Version, created: true); }
        var u = await mgr.UpdateAsync(new ContainerPluginHandle(m.Id, m.Version), m, ct).ConfigureAwait(false);
        return Applied("ContainerPlugin", u.Id, u.Version, created: false);
    }

    private static JsonObject Applied(string kind, string name, string version, bool created) => new()
    {
        ["ok"] = true,
        ["kind"] = kind,
        ["name"] = name,
        ["version"] = version,
        ["action"] = created ? "created" : "updated",
    };

    private static JsonObject Deleted(string kind, string name, string version) => new()
    {
        ["ok"] = true,
        ["kind"] = kind,
        ["name"] = name,
        ["version"] = version,
        ["action"] = "deleted",
    };

    private static JsonObject NotFound(string kind, string name) => new()
    {
        ["ok"] = false,
        ["error"] = $"{kind}/{name} is not registered.",
    };

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };
}
