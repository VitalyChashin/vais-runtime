// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Instantiation;

internal sealed class AgentManifestTranslator : IAgentManifestTranslator
{
    private readonly IAgentRegistry _registry;
    private readonly ICompletionProviderPool _providerPool;
    private readonly IReadOnlyDictionary<(string Name, GuardrailLayer Layer), IGuardrailFactory> _guardrailFactories;
    private readonly IStaticToolRegistry? _staticTools;
    private readonly IPromptTemplateRegistry? _promptTemplates;
    private readonly IPromptFileLoader? _promptFileLoader;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, StatefulAgentOptions> _cache = new(StringComparer.Ordinal);

    public AgentManifestTranslator(
        IAgentRegistry registry,
        ICompletionProviderPool providerPool,
        IEnumerable<IGuardrailFactory> guardrailFactories,
        IServiceProvider serviceProvider,
        IStaticToolRegistry? staticTools = null,
        IPromptTemplateRegistry? promptTemplates = null,
        IPromptFileLoader? promptFileLoader = null)
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
        _serviceProvider = serviceProvider;

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

        // v0.17 declarative-path switch: Model presence. Null Model ⇒ Pillar C (v0.18)
        // handler-loading would need to kick in; until then, 501.
        if (manifest.Model is null)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.HandlerNotLoaded,
                $"Agent '{agentId}' has no ModelSpec and no declarative fields the translator can instantiate. " +
                "Code-authored handlers via AgentHandlerRef.TypeName require the plugin loader that ships with Pillar C (v0.18).");
        }

        // Validate + warm the provider. The pool memoises, so subsequent
        // activations of the same ModelSpec share a single SDK client. Stash
        // the resolved instance on the returned options so AiAgentGrain's
        // activation path picks it up (per-agent providers via the v0.17
        // Pillar B wire-through).
        var provider = await _providerPool.GetAsync(manifest.Model, cancellationToken).ConfigureAwait(false);

        var systemPrompt = await ResolveSystemPromptAsync(manifest.SystemPrompt, cancellationToken).ConfigureAwait(false);
        var toolRegistry = await ResolveToolsAsync(manifest, cancellationToken).ConfigureAwait(false);
        var (inputGuardrails, outputGuardrails, toolGuardrails) = ResolveGuardrails(manifest.Guardrails);

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
        };

        // First-writer-wins: concurrent TranslateAsync calls for the same id
        // do redundant work but converge on a single cached entry.
        _cache.TryAdd(agentId, options);
        return options;
    }

    public StatefulAgentOptions TranslateForGrain(IServiceProvider serviceProvider, string agentId)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // Sync-over-async bridge for ConfigureAgentGrains' Func<string, StatefulAgentOptions>
        // seam. Registry lookup is in-process grain RPC (sub-millisecond); the provider pool
        // is memoised. This is not a web-request hot path — grain activation runs once per
        // agent-id per silo until eviction.
        return TranslateAsync(agentId, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public ValueTask<bool> InvalidateAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return new ValueTask<bool>(_cache.TryRemove(agentId, out _));
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

    private async ValueTask<IToolRegistry?> ResolveToolsAsync(AgentManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Tools is null || manifest.Tools.Count == 0)
        {
            return null;
        }

        var resolved = new List<ITool>();

        foreach (var toolRef in manifest.Tools)
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
                var declared = manifest.McpServers?.Any(m => string.Equals(m.Name, serverName, StringComparison.Ordinal)) ?? false;

                if (!declared)
                {
                    throw new ManifestInstantiationException(
                        ManifestInstantiationUrns.McpServerNotDeclared,
                        $"Tool '{toolRef.Name}' source '{source}' references MCP server '{serverName}', " +
                        "which is not declared in manifest.McpServers.");
                }

                // PR 1 scope: validate declaration only; lazy MCP source construction
                // ships in PR 3 alongside the A2A remote-agents manifest extension and
                // the runtime-host composition-root rewire. A manifest-valid MCP ref
                // at PR 1 returns without contributing a tool.
                //
                // TODO(pillar-b/pr-3): instantiate McpToolSource per declared server,
                // pool per translator, merge discovered tools here.
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
            else
            {
                throw new ManifestInstantiationException(
                    ManifestInstantiationUrns.ToolSourceUnknown,
                    $"Tool '{toolRef.Name}' has unknown source prefix '{source}'. Valid prefixes: 'static:', 'mcp:', 'a2a:'.");
            }
        }

        if (resolved.Count == 0)
        {
            return null;
        }

        return await AggregatingToolRegistry
            .BuildAsync(resolved, sources: null, cancellationToken)
            .ConfigureAwait(false);
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
