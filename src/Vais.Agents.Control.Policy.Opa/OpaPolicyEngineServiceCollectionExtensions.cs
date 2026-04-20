// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Control.Policy.Opa;

/// <summary>
/// DI entry point for the OPA policy-engine adapter. Registers a typed
/// <c>HttpClient</c> against <see cref="OpaPolicyEngineOptions.BaseUrl"/>,
/// wires <see cref="OpaPolicyEngine"/> as the
/// <see cref="IAgentPolicyEngine"/>, and installs
/// <see cref="TimeProvider.System"/> if the container doesn't already
/// carry one.
/// </summary>
public static class OpaPolicyEngineServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OpaPolicyEngine"/> as the runtime's
    /// <see cref="IAgentPolicyEngine"/>.
    /// </summary>
    /// <remarks>
    /// Wires:
    /// <list type="bullet">
    ///   <item><description><see cref="OpaPolicyEngineOptions"/> via the options pattern.</description></item>
    ///   <item><description><see cref="TimeProvider.System"/> when no <see cref="TimeProvider"/> is already registered.</description></item>
    ///   <item><description>A typed <see cref="HttpClient"/> for <see cref="OpaPolicyEngine"/> with <see cref="HttpClient.BaseAddress"/> set to <see cref="OpaPolicyEngineOptions.BaseUrl"/> and <see cref="HttpClient.Timeout"/> set one second beyond <see cref="OpaPolicyEngineOptions.Timeout"/> (per-call timeout is enforced via linked CTS inside the engine).</description></item>
    ///   <item><description><see cref="IAgentPolicyEngine"/> resolving to the singleton <see cref="OpaPolicyEngine"/>.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">Optional options configurator bound to <see cref="OpaPolicyEngineOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddOpaPolicyEngine(
        this IServiceCollection services,
        Action<OpaPolicyEngineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<OpaPolicyEngineOptions>();
        }

        services.TryAddSingleton(TimeProvider.System);

        services
            .AddHttpClient<OpaPolicyEngine>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<OpaPolicyEngineOptions>>().Value;
                client.BaseAddress = options.BaseUrl;
                // HttpClient.Timeout is a long outer bound; per-call timeout
                // lives on a linked CTS inside OpaPolicyEngine. Add slack.
                client.Timeout = options.Timeout + TimeSpan.FromSeconds(1);
            });

        services.TryAddSingleton<IAgentPolicyEngine>(sp => sp.GetRequiredService<OpaPolicyEngine>());

        return services;
    }
}
