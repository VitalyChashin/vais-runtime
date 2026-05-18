// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vais.Agents.Eval.Continuous;

namespace Vais.Agents.Observability.Prometheus;

/// <summary>DI helpers for Prometheus metric sinks.</summary>
public static class AgenticPrometheusExtensions
{
    /// <summary>
    /// Register <see cref="PrometheusSectionSink"/> as an <see cref="ISectionTelemetrySink"/>.
    /// Metrics are written to the default Prometheus registry (<c>Metrics.DefaultRegistry</c>).
    /// Consumers exposing <c>/metrics</c> via <c>UseHttpMetrics()</c> pick them up automatically.
    /// </summary>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddAgenticPrometheusSectionSink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PrometheusSectionSink>();
        services.AddSingleton<ISectionTelemetrySink>(sp => sp.GetRequiredService<PrometheusSectionSink>());
        return services;
    }

    /// <summary>
    /// Register <see cref="PrometheusContinuousScoringMetricSink"/> as the
    /// <see cref="IContinuousScoringMetricSink"/>. Replaces the no-op default registered by
    /// <c>AddVaisAgentsEval</c>. Metrics are written to the default Prometheus registry.
    /// </summary>
    /// <param name="services">The host's DI container.</param>
    public static IServiceCollection AddAgenticPrometheusContinuousScoringMetricSink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<PrometheusContinuousScoringMetricSink>();
        services.AddSingleton<IContinuousScoringMetricSink>(sp => sp.GetRequiredService<PrometheusContinuousScoringMetricSink>());
        return services;
    }
}
