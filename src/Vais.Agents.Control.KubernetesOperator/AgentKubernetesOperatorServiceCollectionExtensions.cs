// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using KubeOps.Operator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// DI entry point for the Kubernetes operator library. Wires the
/// <see cref="AgentEntity"/> controller + finalizer, HTTP client against
/// the v0.6 control plane, ServiceAccount token handler, and secret
/// resolver.
/// </summary>
public static class AgentKubernetesOperatorServiceCollectionExtensions
{
    /// <summary>
    /// Register the Vais.Agents Kubernetes operator into <paramref name="services"/>.
    /// Wires:
    /// <list type="bullet">
    ///   <item><description><see cref="KubernetesOperatorOptions"/> via the options pattern.</description></item>
    ///   <item><description><see cref="TimeProvider.System"/> when no <see cref="TimeProvider"/> is already registered.</description></item>
    ///   <item><description>The KubeOps operator host with <see cref="AgentEntityController"/> + <see cref="AgentEntityFinalizer"/>.</description></item>
    ///   <item><description><see cref="IKubernetesSecretResolver"/> (default <see cref="KubernetesSecretResolver"/>).</description></item>
    ///   <item><description>An <see cref="IAgentControlPlaneClient"/> typed-<see cref="HttpClient"/> pointed at <see cref="KubernetesOperatorOptions.ControlPlaneBaseUrl"/>, with <see cref="ServiceAccountTokenHandler"/> inserted when <see cref="KubernetesOperatorOptions.AuthMode"/> is <see cref="KubernetesOperatorAuthMode.ServiceAccount"/>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">Optional options configurator bound to <see cref="KubernetesOperatorOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddAgentKubernetesOperator(
        this IServiceCollection services,
        Action<KubernetesOperatorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<KubernetesOperatorOptions>();
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddTransient<IKubernetesSecretResolver, KubernetesSecretResolver>();
        services.TryAddTransient<IAgentEntityKubernetesClient, AgentEntityKubernetesClient>();
        services.TryAddTransient<IAgentGraphEntityKubernetesClient, AgentGraphEntityKubernetesClient>();
        services.TryAddTransient<ServiceAccountTokenHandler>();

        services.AddKubernetesOperator()
            .AddController<AgentEntityController, AgentEntity>()
            .AddFinalizer<AgentEntityFinalizer, AgentEntity>(AgentEntity.DeactivateFinalizer)
            .AddController<AgentGraphEntityController, AgentGraphEntity>()
            .AddFinalizer<AgentGraphEntityFinalizer, AgentGraphEntity>(AgentGraphEntity.EvictFinalizer);

        services
            .AddHttpClient<IAgentControlPlaneClient, AgentControlPlaneClient>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<KubernetesOperatorOptions>>().Value;
                if (options.ControlPlaneBaseUrl is null)
                {
                    throw new InvalidOperationException(
                        "KubernetesOperatorOptions.ControlPlaneBaseUrl is required. " +
                        "Set it via AddAgentKubernetesOperator(opts => opts.ControlPlaneBaseUrl = new Uri(\"https://...\")).");
                }
                client.BaseAddress = options.ControlPlaneBaseUrl;
            })
            .AddHttpMessageHandler<ServiceAccountTokenHandler>();

        return services;
    }
}
