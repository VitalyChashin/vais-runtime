// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// <see cref="IPluginReloadHook"/> that invalidates the manifest-translator cache
/// for every agent whose <c>Handler.TypeName</c> belongs to the swapped plugin.
/// Registered automatically by <see cref="AgentManifestInstantiatorServiceCollectionExtensions.AddAgentManifestInstantiator"/>.
/// </summary>
internal sealed class TranslatorInvalidationHook : IPluginReloadHook
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentManifestTranslator _translator;
    private readonly ILogger<TranslatorInvalidationHook> _logger;

    // Must run before GrainReactivationOnPluginReloadHook (Order = 100) so the
    // translator cache is cleared before grains reactivate and re-translate.
    public int Order => 0;

    internal TranslatorInvalidationHook(
        IAgentRegistry registry,
        IAgentManifestTranslator translator,
        ILogger<TranslatorInvalidationHook>? logger = null)
    {
        _registry = registry;
        _translator = translator;
        _logger = logger ?? NullLogger<TranslatorInvalidationHook>.Instance;
    }

    public async Task OnReloadedAsync(PluginReloadResult result, CancellationToken cancellationToken = default)
    {
        var handlerTypes = AffectedAgentResolver.UnionHandlers(result);
        if (handlerTypes.Count == 0) return;

        var count = 0;
        // ListAsync is O(N grain RPCs) on Orleans-backed registries — brief pause on large clusters.
        await foreach (var manifest in _registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (manifest.Handler?.TypeName is { } typeName && handlerTypes.Contains(typeName))
            {
                await _translator.InvalidateAsync(manifest.Id, cancellationToken).ConfigureAwait(false);
                count++;
                _logger.LogDebug(
                    "translator-invalidated: agent '{AgentId}' (handler='{Handler}')",
                    manifest.Id, typeName);
            }
        }

        if (count > 0)
            _logger.LogInformation(
                "translator-invalidated: {Count} agent(s) for plugin '{Plugin}'",
                count, result.NewDescriptor?.Name ?? result.OldDescriptor?.Name);
    }
}
