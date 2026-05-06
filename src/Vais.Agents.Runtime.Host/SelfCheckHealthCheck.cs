// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Readiness check that reflects startup probe results. Tagged <c>ready</c> so it appears
/// in <c>/readyz</c>. Returns <see cref="HealthCheckResult.Unhealthy"/> only when a required
/// service probe failed; optional failures degrade but do not block readiness.
/// Returns <see cref="HealthCheckResult.Degraded"/> while the self-check is still running.
/// </summary>
internal sealed class SelfCheckHealthCheck : IHealthCheck
{
    private readonly SelfCheckResultsStore _store;

    public SelfCheckHealthCheck(SelfCheckResultsStore store) => _store = store;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_store.IsComplete)
            return Task.FromResult(HealthCheckResult.Degraded("Self-check pending"));

        var results = _store.Results;
        var data = results.ToDictionary(r => r.ServiceName, r => (object)(r.IsOk ? "ok" : $"fail: {r.FailureReason}"));

        var requiredFailed = results.Where(r => r.IsRequired && !r.IsOk).ToList();
        if (requiredFailed.Count > 0)
        {
            var msg = $"Required services unreachable: {string.Join(", ", requiredFailed.Select(r => r.ServiceName))}";
            return Task.FromResult(HealthCheckResult.Unhealthy(msg, data: data));
        }

        var optionalFailed = results.Where(r => !r.IsRequired && !r.IsOk).ToList();
        if (optionalFailed.Count > 0)
        {
            var msg = $"Optional services degraded: {string.Join(", ", optionalFailed.Select(r => r.ServiceName))}";
            return Task.FromResult(HealthCheckResult.Degraded(msg, data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("All services reachable", data));
    }
}
