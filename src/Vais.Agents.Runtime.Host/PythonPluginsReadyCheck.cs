// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vais.Agents.Runtime.Plugins.Python;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Readiness check for co-hosted Python plugins. Reports <see cref="HealthCheckResult.Healthy"/>
/// only when every loaded plugin has reached <see cref="PythonPluginStatus.Ready"/>. A plugin
/// in <see cref="PythonPluginStatus.Loading"/> or <see cref="PythonPluginStatus.Restarting"/>
/// yields <see cref="HealthCheckResult.Degraded"/>; any <see cref="PythonPluginStatus.Unavailable"/>
/// plugin yields <see cref="HealthCheckResult.Unhealthy"/>.
/// </summary>
/// <remarks>
/// Registered with the "ready" tag and surfaced on <c>/readyz</c> — the same probe the
/// Helm chart uses as a readiness gate. Wired by <see cref="CompositionRoot"/> only when
/// <c>VAIS_PYTHON_PLUGINS_DIRECTORY</c> is set.
/// </remarks>
internal sealed class PythonPluginsReadyCheck : IHealthCheck
{
    private readonly IPythonPluginHost _host;

    public PythonPluginsReadyCheck(IPythonPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var plugins = _host.LoadedPlugins;

        if (plugins.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("no Python plugins configured"));

        var unavailable = plugins.Where(p => p.Status == PythonPluginStatus.Unavailable).ToList();
        if (unavailable.Count > 0)
        {
            var names = string.Join(", ", unavailable.Select(p => p.Descriptor.Name));
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Python plugin(s) unavailable: {names}",
                data: new Dictionary<string, object> { ["unavailable"] = names }));
        }

        var notReady = plugins.Where(p => p.Status != PythonPluginStatus.Ready).ToList();
        if (notReady.Count > 0)
        {
            var names = string.Join(", ", notReady.Select(p => $"{p.Descriptor.Name}={p.Status}"));
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Python plugin(s) still starting: {names}",
                data: new Dictionary<string, object> { ["notReady"] = names }));
        }

        return Task.FromResult(HealthCheckResult.Healthy($"all {plugins.Count} Python plugin(s) ready"));
    }
}
