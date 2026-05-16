// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Vais.Agents.Core;

namespace Vais.Agents.Observability.OpenTelemetry;

/// <summary>
/// OpenTelemetry wiring helpers for <c>Vais.Agents</c>. Lets consumers register
/// the shared ActivitySource, Meter, and the <see cref="OpenTelemetryUsageSink"/>
/// in one call each.
/// </summary>
public static class AgenticOpenTelemetryExtensions
{
    /// <summary>
    /// Add the <c>Vais.Agents</c> ActivitySource to a <see cref="TracerProviderBuilder"/>.
    /// </summary>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static TracerProviderBuilder AddAgenticInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddSource(AgenticDiagnostics.ActivitySourceName)
            .AddSource("Vais.Agents.Hosting.Orleans")
            .AddSource("Vais.Agents.Core.Graph")
            .AddSource("Vais.Agents.Runtime.Plugins.Python");
    }

    /// <summary>
    /// Add the <c>Vais.Agents</c> Meter to a <see cref="MeterProviderBuilder"/>.
    /// </summary>
    /// <returns>The same builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static MeterProviderBuilder AddAgenticInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(AgenticDiagnostics.MeterName);
    }

    /// <summary>
    /// Register <see cref="OpenTelemetryUsageSink"/> as the default <see cref="IUsageSink"/> singleton.
    /// Consumers still need to attach the <c>Vais.Agents</c> meter to their OTel
    /// <see cref="MeterProviderBuilder"/>; call <see cref="AddAgenticInstrumentation(MeterProviderBuilder)"/>
    /// there. The same applies for traces.
    /// </summary>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddAgenticOpenTelemetrySink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<OpenTelemetryUsageSink>();
        services.TryAddSingleton<IUsageSink>(sp => sp.GetRequiredService<OpenTelemetryUsageSink>());
        return services;
    }

    /// <summary>
    /// Register <see cref="OtelSectionSink"/> as an <see cref="ISectionTelemetrySink"/>. The sink
    /// decorates the per-turn <see cref="System.Diagnostics.Activity"/> with section-breakdown
    /// tags; downstream OTel exporters and Langfuse pick them up automatically.
    /// </summary>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddAgenticOpenTelemetrySectionSink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISectionTelemetrySink>(OtelSectionSink.Instance);
        return services;
    }
}
