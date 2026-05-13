// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Readiness probe for the Orleans silo co-hosted in this process. Resolves
/// <see cref="ILocalSiloDetails"/> plus <see cref="ISiloStatusOracle"/> from DI
/// (both registered by <c>UseOrleans</c>) and reports <see cref="HealthCheckResult.Healthy"/>
/// only when the local silo has transitioned to <see cref="SiloStatus.Active"/>.
/// </summary>
/// <remarks>
/// Mapped to <c>/readyz</c> via <c>MapHealthChecks("/readyz", new() { Predicate = c => c.Tags.Contains("ready") })</c>.
/// The liveness probe (<c>/healthz</c>) intentionally does not consult this check — a silo
/// still joining the cluster is alive, just not ready to accept grain traffic. Failure threshold
/// at 60 s (12 × 5 s) gives ~4× margin over the measured 14 s P99 silo-join time in clustered mode.
/// </remarks>
internal sealed class OrleansActiveHealthCheck : IHealthCheck
{
    private readonly ILocalSiloDetails _silo;
    private readonly ISiloStatusOracle _oracle;

    public OrleansActiveHealthCheck(ILocalSiloDetails silo, ISiloStatusOracle oracle)
    {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(oracle);
        _silo = silo;
        _oracle = oracle;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = _oracle.GetApproximateSiloStatus(_silo.SiloAddress);
        var data = new Dictionary<string, object>
        {
            ["siloName"] = _silo.Name,
            ["siloAddress"] = _silo.SiloAddress.ToString(),
            ["status"] = status.ToString(),
        };

        return Task.FromResult(status == SiloStatus.Active
            ? HealthCheckResult.Healthy($"silo {_silo.Name} active", data)
            : HealthCheckResult.Unhealthy($"silo {_silo.Name} status={status}", data: data));
    }
}
