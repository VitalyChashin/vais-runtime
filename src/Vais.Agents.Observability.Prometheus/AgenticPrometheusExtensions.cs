// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Observability.Prometheus;

/// <summary>DI helpers for <see cref="PrometheusSectionSink"/>.</summary>
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
}
