// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Control;
using Vais.Agents.Core;
using Vais.Agents.Gateways.McpGovernance;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Runtime.Instantiation;

internal sealed class AgentManifestTranslator : IAgentManifestTranslator
{
    private readonly IAgentRegistry _registry;
    private readonly ICompletionProviderPool _providerPool;
    private readonly IReadOnlyDictionary<(string Name, GuardrailLayer Layer), IGuardrailFactory> _guardrailFactories;
    private readonly IStaticToolRegistry? _staticTools;
    private readonly IPromptTemplateRegistry? _promptTemplates;
    private readonly IPromptFileLoader? _promptFileLoader;
    private readonly IPluginHandlerRegistry? _pluginRegistry;
    private readonly IManifestApplyDiagnosticsSink? _diagnosticsSink;
    private readonly IServiceProvider _serviceProvider;
    private readonly IReadOnlyList<INamedToolSourceProvider> _toolSourceProviders;
    private readonly ILlmGatewayConfigRegistry? _llmGatewayConfigRegistry;
    private readonly IMcpGatewayConfigRegistry? _mcpGatewayConfigRegistry;
    private readonly IMcpServerRegistry? _mcpServerRegistry;
    private readonly ILlmGatewayMiddlewareFactory? _llmGatewayFactory;
    private readonly IToolGatewayMiddlewareFactory? _toolGatewayFactory;
    private readonly ILogger<AgentManifestTranslator> _logger;
    private readonly ConcurrentDictionary<string, StatefulAgentOptions> _cache = new(StringComparer.Ordinal);

    public AgentManifestTranslator(
        IAgentRegistry registry,
        ICompletionProviderPool providerPool,
        IEnumerable<IGuardrailFactory> guardrailFactories,
        IServiceProvider serviceProvider,
        IStaticToolRegistry? staticTools = null,
        IPromptTemplateRegistry? promptTemplates = null,
        IPromptFileLoader? promptFileLoader = null,
        IPluginHandlerRegistry? pluginRegistry = null,
        IManifestApplyDiagnosticsSink? diagnosticsSink = null,
        IEnumerable<INamedToolSourceProvider>? toolSourceProviders = null,
        ILlmGatewayConfigRegistry? llmGatewayConfigRegistry = null,
        IMcpGatewayConfigRegistry? mcpGatewayConfigRegistry = null,
        IMcpServerRegistry? mcpServerRegistry = null,
        ILlmGatewayMiddlewareFactory? llmGatewayFactory = null,
        IToolGatewayMiddlewareFactory? toolGatewayFactory = null,
        ILogger<AgentManifestTranslator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(providerPool);
        ArgumentNullException.ThrowIfNull(guardrailFactories);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        _registry = registry;
        _providerPool = providerPool;
        _staticTools = staticTools;
        _promptTemplates = promptTemplates;
        _promptFileLoader = promptFileLoader;
        _pluginRegistry = pluginRegistry;
        _diagnosticsSink = diagnosticsSink;
        _serviceProvider = serviceProvider;
        _toolSourceProviders = toolSourceProviders?.ToList() ?? [];
        _llmGatewayConfigRegistry = llmGatewayConfigRegistry;
        _mcpGatewayConfigRegistry = mcpGatewayConfigRegistry;
        _mcpServerRegistry = mcpServerRegistry;
        _llmGatewayFactory = llmGatewayFactory;
        _toolGatewayFactory = toolGatewayFactory;
        _logger = logger ?? NullLogger<AgentManifestTranslator>.Instance;

        var map = new Dictionary<(string, GuardrailLayer), IGuardrailFactory>();
        foreach (var factory in guardrailFactories)
        {
            var key = (factory.Name, factory.Layer);
            if (!map.TryAdd(key, factory))
            {
                throw new InvalidOperationException(
                    $"Duplicate IGuardrailFactory registered for ({factory.Name}, {factory.Layer}). Each (name, layer) pair must be unique.");
            }
        }
        _guardrailFactories = map;
    }

    public async ValueTask<StatefulAgentOptions> TranslateAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        if (_cache.TryGetValue(agentId, out var cached))
        {
            return cached;
        }

        var manifest = await _registry.GetAsync(agentId, version: null, cancellationToken).ConfigureAwait(false)
            ?? throw new ManifestInstantiationException(
                ManifestInstantiationUrns.AgentNotFound,
                $"No manifest registered for agent id '{agentId}'.");

        // v0.18 Pillar C plugin branch: if the manifest's Handler.TypeName
        // matches a loaded plugin factory, route to the plugin and stash the
        // resulting IAiAgent on StatefulAgentOptions. If Model is also set,
        // record an apply-time WARN — plugin wins; declarative fields ignored.
        if (_pluginRegistry is not null
            && _pluginRegistry.TryGet(manifest.Handler.TypeName, out var factory)
            && factory is not null)
        {
            // When the manifest carries a ModelSpec, build ICompletionProvider from the pool
            // and pass it via a wrapped IServiceProvider so plugin constructors that declare
            // ICompletionProvider as a dependency are satisfied (it is never a DI singleton).
            // Other declarative fields (tools, guardrails, systemPrompt) are not applied to
            // plugin agents — the plugin implementation owns its own configuration.
            IServiceProvider factoryProvider = _serviceProvider;
            if (manifest.Model is not null)
            {
                _diagnosticsSink?.Record(
                    agentId,
                    ManifestInstantiationUrns.HandlerAndDeclarativeFieldsBothSet,
                    $"Agent '{agentId}' has both a plugin handler ('{manifest.Handler.TypeName}') and declarative Model fields set. Plugin wins; declarative fields are ignored.");

                var completionProvider = await _providerPool.GetAsync(manifest.Model, cancellationToken).ConfigureAwait(false);
                factoryProvider = new CompletionProviderScope(_serviceProvider, completionProvider);
            }

            IAiAgent pluginAgent;
            try
            {
                pluginAgent = await factory.CreateAsync(manifest, factoryProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.PluginFactoryThrow,
                    $"Plugin factory for handler '{manifest.Handler.TypeName}' threw while activating agent '{agentId}': {ex.Message}",
                    ex);
            }

            var pluginToolGatewayMiddleware = _serviceProvider.GetServices<ToolGatewayMiddleware>().ToArray();
            var pluginOptions = new StatefulAgentOptions
            {
                AgentName = manifest.Id,
                Agent = pluginAgent,
                Budget = manifest.Budget,
                ToolGatewayMiddleware = pluginToolGatewayMiddleware,
            };

            _cache.TryAdd(agentId, pluginOptions);
            return pluginOptions;
        }

        // v0.17 declarative-path switch: Model presence. Null Model with no
        // matching plugin ⇒ the manifest references a code-authored handler
        // we can't load.
        if (manifest.Model is null)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.HandlerNotLoaded,
                $"Agent '{agentId}' has no ModelSpec and no loaded plugin handler for " +
                $"AgentHandlerRef.TypeName '{manifest.Handler.TypeName}'. Ship a plugin that exports " +
                "this handler, or switch the manifest to the declarative path with a ModelSpec.");
        }

        // Validate + warm the provider. The pool memoises, so subsequent
        // activations of the same ModelSpec share a single SDK client. Stash
        // the resolved instance on the returned options so AiAgentGrain's
        // activation path picks it up (per-agent providers via the v0.17
        // Pillar B wire-through).
        var provider = await _providerPool.GetAsync(manifest.Model, cancellationToken).ConfigureAwait(false);

        var systemPrompt = await ResolveSystemPromptAsync(manifest.SystemPrompt, cancellationToken).ConfigureAwait(false);
        var (inputGuardrails, outputGuardrails, toolGuardrails) = ResolveGuardrails(manifest.Guardrails);

        // GCF-22: LlmGatewayRef — per-agent pipeline replaces DI-global chain entirely.
        // Agents without llmGatewayRef continue to use the DI-global chain unchanged.
        LlmGatewayMiddleware[] gatewayMiddleware;
        if (_llmGatewayConfigRegistry is not null && _llmGatewayFactory is not null
            && manifest.LlmGatewayRef is { } llmRef)
        {
            var llmCfg = await _llmGatewayConfigRegistry.GetAsync(llmRef, ct: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Agent '{manifest.Id}' references LlmGatewayConfig '{llmRef}' which is not registered.");
            gatewayMiddleware = llmCfg.Middleware
                .Select(spec => _llmGatewayFactory.Create(spec))
                .ToArray();
        }
        else
        {
            gatewayMiddleware = _serviceProvider.GetServices<LlmGatewayMiddleware>().ToArray();
        }

        // GCF-24: Expand transport:registered McpServerRefs into IToolSources.
        // Virtual servers → VirtualMcpToolSource; physical servers → INamedToolSourceProvider bridge.
        // Physical registered servers are served by PhysicalMcpConnectionService (Vais.Agents.Control.Mcp)
        // which registers as INamedToolSourceProvider and is resolved below in ResolveToolsAsync.
        // Also collects ServerMcpGatewayRef for GCF-23 Option D in a single registry pass.
        var registered = await ResolveRegisteredMcpSourcesAsync(manifest, cancellationToken).ConfigureAwait(false);

        // GCF-23: McpGatewayRef — per-agent pipeline replaces DI-global chain entirely.
        // Agent-level ref wins; if absent, server-level ref applies (Option D); else DI-global.
        // "ToolWorkspacePolicy" is a special case: workspace policies come from the manifest,
        // not from GatewayMiddlewareSpec.Params (the factory is a no-op sentinel).
        ToolGatewayMiddleware[] toolGatewayMiddleware;
        if (_mcpGatewayConfigRegistry is not null && _toolGatewayFactory is not null)
        {
            if (manifest.McpGatewayRef is { } mcpGwRef)
            {
                toolGatewayMiddleware = await BuildToolGatewayMiddlewareAsync(
                    mcpGwRef, manifest.Id, cancellationToken).ConfigureAwait(false);
            }
            else if (registered.McpGatewayRefAmbiguous)
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.McpGatewayRefAmbiguous,
                    $"Agent '{manifest.Id}' binds multiple registered servers that carry different " +
                    "McpGatewayRef values. Set an agent-level mcpGatewayRef to resolve the ambiguity.");
            }
            else if (registered.ServerMcpGatewayRef is { } serverGwRef)
            {
                toolGatewayMiddleware = await BuildToolGatewayMiddlewareAsync(
                    serverGwRef, manifest.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                toolGatewayMiddleware = _serviceProvider.GetServices<ToolGatewayMiddleware>().ToArray();
            }
        }
        else
        {
            toolGatewayMiddleware = _serviceProvider.GetServices<ToolGatewayMiddleware>().ToArray();
        }

        var toolRegistry = await ResolveToolsAsync(manifest, registered.Sources, cancellationToken).ConfigureAwait(false);

        var responseFormat = ResolveResponseFormat(manifest, provider);

        var options = new StatefulAgentOptions
        {
            AgentName = manifest.Id,
            CompletionProvider = provider,
            SystemPrompt = systemPrompt,
            ToolRegistry = toolRegistry,
            InputGuardrails = inputGuardrails,
            OutputGuardrails = outputGuardrails,
            ToolGuardrails = toolGuardrails,
            Budget = manifest.Budget,
            GatewayMiddleware = gatewayMiddleware,
            ToolGatewayMiddleware = toolGatewayMiddleware,
            UsageSink = _serviceProvider.GetService<IUsageSink>(),
            ResponseFormat = responseFormat,
        };

        _logger.LogDebug(
            "Translated agent {AgentId}: llm-middleware=[{LlmMiddleware}] tool-middleware=[{ToolMiddleware}]",
            agentId,
            string.Join(", ", gatewayMiddleware.Select(m => m.GetType().Name)),
            string.Join(", ", toolGatewayMiddleware.Select(m => m.GetType().Name)));

        // First-writer-wins: concurrent TranslateAsync calls for the same id
        // do redundant work but converge on a single cached entry.
        _cache.TryAdd(agentId, options);
        return options;
    }

    public ValueTask<StatefulAgentOptions> TranslateForGrain(IServiceProvider serviceProvider, string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return TranslateAsync(agentId, cancellationToken);
    }

    public ValueTask<bool> InvalidateAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return new ValueTask<bool>(_cache.TryRemove(agentId, out _));
    }

    private ResponseFormatSpec? ResolveResponseFormat(AgentManifest manifest, ICompletionProvider provider)
    {
        if (manifest.OutputSchema is not { ValueKind: System.Text.Json.JsonValueKind.Object } schema)
            return null;

        if (!provider.SupportsResponseFormat)
        {
            _logger.LogInformation(
                "Agent '{AgentId}': OutputSchema is set but provider '{ProviderName}' does not support " +
                "response_format — continuing with prompt-driven enforcement.",
                manifest.Id, provider.ProviderName);
            return null;
        }

        var hasTools = (manifest.Tools is { Count: > 0 }) || (manifest.McpServers is { Count: > 0 });
        if (hasTools)
        {
            _logger.LogWarning(
                "Agent '{AgentId}': OutputSchema and tools are both configured. " +
                "response_format will be dropped for turns that include tools (soft-degrade). " +
                "Schema enforcement applies to schema-only turns only.",
                manifest.Id);
        }

        return new ResponseFormatSpec(schema, SchemaName: manifest.Id);
    }

    private async ValueTask<string?> ResolveSystemPromptAsync(SystemPromptSpec? spec, CancellationToken cancellationToken)
    {
        if (spec is null)
        {
            return null;
        }

        var shapeCount = (spec.Inline is not null ? 1 : 0)
            + (spec.TemplateRef is not null ? 1 : 0)
            + (spec.FileRef is not null ? 1 : 0);

        if (shapeCount > 1)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.PromptSpecAmbiguous,
                "SystemPromptSpec may specify at most one of Inline / TemplateRef / FileRef.");
        }

        string? raw = null;

        if (spec.Inline is not null)
        {
            raw = spec.Inline;
        }
        else if (spec.TemplateRef is not null)
        {
            if (_promptTemplates is null)
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.PromptTemplateNotRegistered,
                    $"SystemPromptSpec.TemplateRef '{spec.TemplateRef}' requested but no IPromptTemplateRegistry is registered in DI.");
            }

            raw = _promptTemplates.Get(spec.TemplateRef)
                ?? throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.PromptTemplateNotRegistered,
                    $"SystemPromptSpec.TemplateRef '{spec.TemplateRef}' does not resolve in the registered IPromptTemplateRegistry.");
        }
        else if (spec.FileRef is not null)
        {
            if (_promptFileLoader is null)
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.PromptFileUnreadable,
                    $"SystemPromptSpec.FileRef '{spec.FileRef}' requested but no IPromptFileLoader is registered in DI.");
            }

            raw = await _promptFileLoader.LoadAsync(spec.FileRef, cancellationToken).ConfigureAwait(false);
        }

        if (raw is null)
        {
            return null;
        }

        if (spec.Variables is { Count: > 0 })
        {
            foreach (var (key, value) in spec.Variables)
            {
                raw = raw.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
            }
        }

        return raw;
    }

    private async ValueTask<ToolGatewayMiddleware[]> BuildToolGatewayMiddlewareAsync(
        string mcpGwRef, string agentId, CancellationToken cancellationToken)
    {
        var mcpCfg = await _mcpGatewayConfigRegistry!.GetAsync(mcpGwRef, ct: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Agent '{agentId}' references McpGatewayConfig '{mcpGwRef}' which is not registered.");

        var workspacePolicies = mcpCfg.WorkspacePolicies?
            .ToDictionary(
                kv => kv.Key,
                kv => new WorkspaceToolPolicy(
                    (IReadOnlyList<string>)(kv.Value.AllowedTools ?? []),
                    (IReadOnlyList<string>)(kv.Value.DeniedTools ?? []),
                    kv.Value.MinPrivilegeLevel));

        return mcpCfg.Middleware
            .Select(spec =>
                spec.Name == "ToolWorkspacePolicy" && workspacePolicies is not null
                    ? (ToolGatewayMiddleware)new ToolWorkspacePolicyMiddleware(workspacePolicies)
                    : _toolGatewayFactory!.Create(spec))
            .ToArray();
    }

    private sealed record RegisteredMcpResolution(
        IReadOnlyDictionary<string, IToolSource> Sources,
        string? ServerMcpGatewayRef,
        bool McpGatewayRefAmbiguous);

    private async ValueTask<RegisteredMcpResolution> ResolveRegisteredMcpSourcesAsync(
        AgentManifest manifest, CancellationToken cancellationToken)
    {
        if (_mcpServerRegistry is null || manifest.McpServers is not { Count: > 0 })
            return new RegisteredMcpResolution(
                new Dictionary<string, IToolSource>(StringComparer.Ordinal),
                ServerMcpGatewayRef: null,
                McpGatewayRefAmbiguous: false);

        var result = new Dictionary<string, IToolSource>(StringComparer.Ordinal);
        var distinctGatewayRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var serverRef in manifest.McpServers)
        {
            if (serverRef.Transport != McpServerRef.RegisteredTransport) continue;

            var srv = await _mcpServerRegistry.GetAsync(serverRef.Name, ct: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Agent '{manifest.Id}' references McpServer '{serverRef.Name}' which is not registered.");

            if (srv.Virtual)
            {
                result[serverRef.Name] = BuildVirtualMcpToolSource(srv);
            }
            // Physical servers: delegated to INamedToolSourceProvider at tool-resolution time.
            // PhysicalMcpConnectionService (Vais.Agents.Control.Mcp) serves them; if that service
            // is not registered, GetByName returns null and tool resolution throws McpServerUnavailable.

            if (srv.McpGatewayRef is { } gwRef)
                distinctGatewayRefs.Add(gwRef);
        }

        var serverGwRef = distinctGatewayRefs.Count == 1 ? distinctGatewayRefs.First() : (string?)null;
        return new RegisteredMcpResolution(result, serverGwRef, distinctGatewayRefs.Count > 1);
    }

    private VirtualMcpToolSource BuildVirtualMcpToolSource(McpServerManifest srv)
    {
        var upstreamSources = new List<(IToolSource Source, string ServerId)>();
        foreach (var sourceRef in srv.Sources ?? [])
        {
            IToolSource? upstreamSource = null;
            foreach (var provider in _toolSourceProviders)
            {
                upstreamSource = provider.GetByName(sourceRef.Ref);
                if (upstreamSource is not null) break;
            }
            if (upstreamSource is null)
                throw new InvalidOperationException(
                    $"Virtual MCP server '{srv.Id}' references source '{sourceRef.Ref}' which is unavailable. " +
                    $"No INamedToolSourceProvider has a ready source for '{sourceRef.Ref}'.");
            upstreamSources.Add((upstreamSource, sourceRef.Ref));
        }
        return new VirtualMcpToolSource(upstreamSources, srv.ToolProjection);
    }

    private async ValueTask<IToolRegistry?> ResolveToolsAsync(
        AgentManifest manifest,
        IReadOnlyDictionary<string, IToolSource> registeredSources,
        CancellationToken cancellationToken)
    {
        // D1: a registered server is in explicit mode if any tools[] entry has source == "mcp:<serverName>".
        // All other transport:registered servers are in import-all mode.
        var explicitServers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolRef in manifest.Tools ?? [])
        {
            if (toolRef.Source?.StartsWith("mcp:", StringComparison.Ordinal) == true)
                explicitServers.Add(toolRef.Source["mcp:".Length..]);
        }

        var importAllRefs = manifest.McpServers?
            .Where(s => s.Transport == McpServerRef.RegisteredTransport && !explicitServers.Contains(s.Name))
            .ToList() ?? [];

        if ((manifest.Tools is null || manifest.Tools.Count == 0) && importAllRefs.Count == 0)
            return null;

        // Per-call caches so each server's IToolSource and discovered tool list is fetched once.
        // Pre-populate with registered sources (transport:registered virtual → VirtualMcpToolSource).
        var mcpSourceCache = new Dictionary<string, IToolSource?>(StringComparer.Ordinal);
        foreach (var kv in registeredSources) mcpSourceCache[kv.Key] = kv.Value;
        var mcpToolsCache = new Dictionary<string, List<ITool>>(StringComparer.Ordinal);

        // --- Phase 1: explicit tools[] entries ---
        var resolved = new List<ITool>();

        foreach (var toolRef in manifest.Tools ?? [])
        {
            var source = toolRef.Source ?? string.Empty;

            if (source.StartsWith("static:", StringComparison.Ordinal))
            {
                var name = source["static:".Length..];
                if (_staticTools is null)
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.ToolNotRegistered,
                        $"Tool '{toolRef.Name}' source '{source}' requested but no IStaticToolRegistry is registered in DI.");
                }

                var tool = _staticTools.Get(name, _serviceProvider)
                    ?? throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.ToolNotRegistered,
                        $"Static tool '{name}' not registered in IStaticToolRegistry. Referenced by tool '{toolRef.Name}'.");

                resolved.Add(tool);
            }
            else if (source.StartsWith("mcp:", StringComparison.Ordinal))
            {
                var serverName = source["mcp:".Length..];

                // Server must be explicitly declared — acts as a security boundary.
                // For Python plugin-backed servers the transport/command fields are ignored
                // (the plugin subprocess is already running); only Name is used for lookup.
                var serverRef = manifest.McpServers?.FirstOrDefault(
                    m => string.Equals(m.Name, serverName, StringComparison.Ordinal));

                if (serverRef is null)
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.McpServerNotDeclared,
                        $"Tool '{toolRef.Name}' source '{source}' references MCP server '{serverName}', " +
                        "which is not declared in manifest.McpServers.");
                }

                // Honor the per-server tool allowlist.
                if (serverRef.Tools is { Count: > 0 } &&
                    !serverRef.Tools.Contains(toolRef.Name, StringComparer.Ordinal))
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.McpToolNotFound,
                        $"Tool '{toolRef.Name}' is not permitted by the allowlist for MCP server '{serverName}'. " +
                        $"Allowed tools: [{string.Join(", ", serverRef.Tools)}].");
                }

                // Look up or cache the IToolSource for this server.
                if (!mcpSourceCache.TryGetValue(serverName, out var toolSource))
                {
                    foreach (var provider in _toolSourceProviders)
                    {
                        toolSource = provider.GetByName(serverName);
                        if (toolSource is not null)
                            break;
                    }
                    mcpSourceCache[serverName] = toolSource; // null = unavailable
                }

                if (toolSource is null)
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.McpServerUnavailable,
                        $"MCP server '{serverName}' is declared but unavailable. " +
                        $"No INamedToolSourceProvider has a ready source for '{serverName}'.");
                }

                // Discover and cache the tool list for this server.
                if (!mcpToolsCache.TryGetValue(serverName, out var discoveredTools))
                {
                    discoveredTools = [];
                    await foreach (var t in toolSource.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                        discoveredTools.Add(t);
                    mcpToolsCache[serverName] = discoveredTools;
                }

                // NOTE: StatefulAgentOptions is cached per agentId. If the Python plugin
                // supervisor restarts (new McpClient), cached McpBackedTool instances hold a
                // stale client and invocations will fail. A future hook analogous to
                // TranslatorInvalidationHook (v0.22 C# plugin reloads) must listen to
                // supervisor restart events and evict the relevant cache entry.
                // TODO(pillar-b/pr-N): PythonPluginRestartInvalidationHook.

                var mcpTool = discoveredTools.FirstOrDefault(
                    t => string.Equals(t.Name, toolRef.Name, StringComparison.Ordinal))
                    ?? throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.McpToolNotFound,
                        $"MCP server '{serverName}' does not expose tool '{toolRef.Name}'. " +
                        $"Available: [{string.Join(", ", discoveredTools.Select(t => t.Name))}].");

                resolved.Add(mcpTool);
            }
            else if (source.StartsWith("a2a:", StringComparison.Ordinal))
            {
                var agentName = source["a2a:".Length..];
                var declared = manifest.A2ARemoteAgents?.Any(a => string.Equals(a.Name, agentName, StringComparison.Ordinal)) ?? false;

                if (!declared)
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.A2AAgentNotDeclared,
                        $"Tool '{toolRef.Name}' source '{source}' references A2A remote agent '{agentName}', " +
                        "which is not declared in manifest.A2ARemoteAgents.");
                }

                // PR 3 scope: validate declaration only. Lazy A2ARemoteAgentTool
                // construction lands with the broader outbound-A2A productisation.
                // TODO(pillar-b/v0.17.x): instantiate A2ARemoteAgentTool per declared
                // A2ARemoteAgentRef, pool per translator, merge into the registry.
            }
            else if (source.StartsWith("agent:", StringComparison.Ordinal))
            {
                var agentName = source["agent:".Length..];
                var localRef = manifest.LocalAgents?.FirstOrDefault(
                    a => string.Equals(a.Name, agentName, StringComparison.Ordinal));

                if (localRef is null)
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.LocalAgentNotDeclared,
                        $"Tool '{toolRef.Name}' source '{source}' references local agent '{agentName}', " +
                        "which is not declared in manifest.LocalAgents.");
                }

                var effectiveAgentId = localRef.AgentId ?? localRef.Name;

                // Verify target exists in the registry at translate time for fail-fast
                // URN errors. Version pinning resolves to null = latest.
                var targetManifest = await _registry.GetAsync(
                    effectiveAgentId, localRef.AgentVersion, cancellationToken).ConfigureAwait(false);
                if (targetManifest is null)
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.LocalAgentTargetNotFound,
                        $"Tool '{toolRef.Name}' source '{source}': target agent id '{effectiveAgentId}'" +
                        (localRef.AgentVersion is not null ? $" version '{localRef.AgentVersion}'" : string.Empty) +
                        " is not found in the registry.");
                }

                var description = localRef.Description ?? targetManifest.Description ?? string.Empty;

                if (localRef.Mode == LocalAgentInvocationMode.Blocking)
                {
                    // Lazy IAgentRuntime via _serviceProvider to avoid a DI construction cycle
                    // (IAgentRuntime → IAgentManifestTranslator → LocalAgentTool → IAgentRuntime).
                    IAgentRuntime RuntimeFactory() =>
                        _serviceProvider.GetRequiredService<IAgentRuntime>();

                    resolved.Add(new LocalAgentTool(
                        RuntimeFactory,
                        effectiveAgentId,
                        toolRef.Name,
                        description,
                        localRef.AllowCallerSuppliedSession,
                        localRef.PropagateAllowedTools));
                }
                else
                {
                    // Background mode: emit BackgroundLocalAgentTool + idempotently add management tools.
                    var tracker = _serviceProvider.GetService<IBackgroundAgentTracker>()
                        ?? throw new ManifestInstantiationException(
                            ManifestInstantiationUrns.ToolSourceUnknown,
                            $"Tool '{toolRef.Name}' source '{source}': Background mode requires " +
                            "IBackgroundAgentTracker to be registered in DI (e.g. InMemoryBackgroundAgentTracker " +
                            "or OrleansBackgroundAgentTracker).");

                    IAgentRuntime BackgroundRuntimeFactory() =>
                        _serviceProvider.GetRequiredService<IAgentRuntime>();

                    resolved.Add(new BackgroundLocalAgentTool(
                        BackgroundRuntimeFactory,
                        tracker,
                        effectiveAgentId,
                        toolRef.Name,
                        description,
                        localRef.AllowCallerSuppliedSession,
                        localRef.PropagateAllowedTools));

                    // Add management tools once per translator build (idempotent by name).
                    if (!resolved.Any(t => t.Name == "list_background_agents"))
                    {
                        foreach (var mgmt in BackgroundAgentManagementTools.Create(tracker))
                            resolved.Add(mgmt);
                    }
                }
            }
            else
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.ToolSourceUnknown,
                    $"Tool '{toolRef.Name}' has unknown source prefix '{source}'. Valid prefixes: 'static:', 'mcp:', 'a2a:', 'agent:'.");
            }
        }

        // --- Phase 2: import-all mode (D1) — registered servers with no explicit tools[] entry ---
        var importAllResolved = await ResolveImportAllToolsAsync(
            importAllRefs, mcpSourceCache, mcpToolsCache, cancellationToken).ConfigureAwait(false);

        // D3: fail-fast when two import-all servers expose the same tool name.
        // Explicit-vs-explicit and explicit-vs-import-all collisions use first-wins (AggregatingToolRegistry).
        var importAllNames = new Dictionary<string, string>(StringComparer.Ordinal); // toolName → serverName
        foreach (var (tool, serverName) in importAllResolved)
        {
            if (!importAllNames.TryAdd(tool.Name, serverName))
            {
                var existingServer = importAllNames[tool.Name];
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.McpToolNameCollision,
                    $"Tool name '{tool.Name}' is exposed by multiple import-all servers: " +
                    $"'{existingServer}' and '{serverName}'. " +
                    "Narrow one server using McpServerRef.Tools, or add an explicit tools[] entry to put it in explicit mode.");
            }
        }

        // Explicit tools first — they win over import-all tools on name collision (first-wins).
        var allTools = new List<ITool>(resolved.Count + importAllResolved.Count);
        allTools.AddRange(resolved);
        allTools.AddRange(importAllResolved.Select(x => x.Tool));

        if (allTools.Count == 0)
            return null;

        return await AggregatingToolRegistry
            .BuildAsync(allTools, sources: null, cancellationToken)
            .ConfigureAwait(false);
    }

    // D1 import-all phase: for each registered server not in explicit mode, discover and import
    // its full toolset (narrowed by McpServerRef.Tools allowlist when specified — D2).
    private async ValueTask<List<(ITool Tool, string ServerName)>> ResolveImportAllToolsAsync(
        List<McpServerRef> importAllRefs,
        Dictionary<string, IToolSource?> mcpSourceCache,
        Dictionary<string, List<ITool>> mcpToolsCache,
        CancellationToken cancellationToken)
    {
        var result = new List<(ITool Tool, string ServerName)>();

        foreach (var serverRef in importAllRefs)
        {
            var serverName = serverRef.Name;

            // Resolve IToolSource: virtual servers are pre-populated in mcpSourceCache from
            // registeredSources; physical servers are resolved lazily via INamedToolSourceProvider.
            if (!mcpSourceCache.TryGetValue(serverName, out var toolSource))
            {
                foreach (var provider in _toolSourceProviders)
                {
                    toolSource = provider.GetByName(serverName);
                    if (toolSource is not null) break;
                }
                mcpSourceCache[serverName] = toolSource;
            }

            if (toolSource is null)
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.McpServerUnavailable,
                    $"MCP server '{serverName}' is declared but unavailable. " +
                    $"No INamedToolSourceProvider has a ready source for '{serverName}'.");
            }

            if (!mcpToolsCache.TryGetValue(serverName, out var discoveredTools))
            {
                discoveredTools = [];
                await foreach (var t in toolSource.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                    discoveredTools.Add(t);
                mcpToolsCache[serverName] = discoveredTools;
            }

            // D2: McpServerRef.Tools allowlist narrows the import. Every allowlisted name must exist.
            if (serverRef.Tools is { Count: > 0 })
            {
                var allowSet = new HashSet<string>(serverRef.Tools, StringComparer.Ordinal);
                foreach (var allowedName in serverRef.Tools)
                {
                    if (!discoveredTools.Any(t => string.Equals(t.Name, allowedName, StringComparison.Ordinal)))
                        throw new ManifestInstantiationException(
                            ManifestInstantiationUrns.McpToolNotFound,
                            $"Tool '{allowedName}' in McpServerRef.Tools allowlist for server '{serverName}' " +
                            $"was not found. Available: [{string.Join(", ", discoveredTools.Select(t => t.Name))}].");
                }
                foreach (var tool in discoveredTools.Where(t => allowSet.Contains(t.Name)))
                    result.Add((tool, serverName));
            }
            else
            {
                foreach (var tool in discoveredTools)
                    result.Add((tool, serverName));
            }
        }

        return result;
    }

    // Wraps the host IServiceProvider to surface a pre-built ICompletionProvider so that
    // plugin constructors declaring ICompletionProvider as a dependency can be satisfied
    // by ActivatorUtilities.CreateInstance without registering a container singleton.
    private sealed class CompletionProviderScope(IServiceProvider inner, ICompletionProvider provider) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICompletionProvider) ? provider : inner.GetService(serviceType);
    }

    private (IReadOnlyList<IInputGuardrail> Input, IReadOnlyList<IOutputGuardrail> Output, IReadOnlyList<IToolGuardrail> Tool)
        ResolveGuardrails(GuardrailsSpec? spec)
    {
        if (spec is null)
        {
            return (Array.Empty<IInputGuardrail>(), Array.Empty<IOutputGuardrail>(), Array.Empty<IToolGuardrail>());
        }

        return (
            Resolve<IInputGuardrail>(spec.Input, GuardrailLayer.Input),
            Resolve<IOutputGuardrail>(spec.Output, GuardrailLayer.Output),
            Resolve<IToolGuardrail>(spec.Tool, GuardrailLayer.Tool));

        IReadOnlyList<T> Resolve<T>(IReadOnlyList<GuardrailRef>? refs, GuardrailLayer layer)
        {
            if (refs is null || refs.Count == 0)
            {
                return Array.Empty<T>();
            }

            var results = new List<T>(refs.Count);
            foreach (var reference in refs)
            {
                if (!_guardrailFactories.TryGetValue((reference.Name, layer), out var factory))
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.GuardrailNotRegistered,
                        $"No IGuardrailFactory registered for guardrail '{reference.Name}' at layer {layer}.");
                }

                var instance = factory.Create(reference.Params, _serviceProvider);
                if (instance is not T typed)
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.GuardrailNotRegistered,
                        $"IGuardrailFactory '{reference.Name}' for layer {layer} returned an instance of type " +
                        $"'{instance.GetType().FullName}' which does not implement '{typeof(T).FullName}'.");
                }

                results.Add(typed);
            }

            return results;
        }
    }
}
