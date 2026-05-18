// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// <see cref="IPluginReloadHook"/> that deactivates Orleans agent grains whose handler
/// type belongs to a reloaded plugin. On the next invocation each grain reactivates
/// against the updated handler in the plugin registry.
/// Must be registered together with <c>TranslatorInvalidationHook</c> (Order = 0) so
/// the translator cache is cleared before the grain reactivates and re-translates.
/// </summary>
internal sealed class GrainReactivationOnPluginReloadHook : IPluginReloadHook
{
    private readonly IAgentRegistry _registry;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<GrainReactivationOnPluginReloadHook> _logger;

    public int Order => 100;

    internal GrainReactivationOnPluginReloadHook(
        IAgentRegistry registry,
        IGrainFactory grainFactory,
        ILogger<GrainReactivationOnPluginReloadHook>? logger = null)
    {
        _registry = registry;
        _grainFactory = grainFactory;
        _logger = logger ?? NullLogger<GrainReactivationOnPluginReloadHook>.Instance;
    }

    public async Task OnReloadedAsync(PluginReloadResult result, CancellationToken cancellationToken = default)
    {
        var handlerTypes = AffectedAgentResolver.UnionHandlers(result);
        if (handlerTypes.Count == 0) return;

        var count = 0;
        await foreach (var manifest in _registry.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (manifest.Handler?.TypeName is { } typeName && handlerTypes.Contains(typeName))
            {
                var grain = _grainFactory.GetGrain<IAiAgentGrain>(manifest.Id);
                await grain.RequestDeactivationAsync().ConfigureAwait(false);
                count++;

                // Throttle to 50 deactivations/sec to avoid a thundering-herd spike on
                // large clusters. Acceptable brief delay on bigger fleets.
                if (count % 50 == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        if (count > 0)
            _logger.LogInformation(
                "grain-reactivated: {Count} agent(s) deactivated for plugin '{Plugin}' reload",
                count, result.NewDescriptor?.Name ?? result.OldDescriptor?.Name);
    }
}
