// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Vais.Agents.Identity.Oidc;

/// <summary>
/// DI entry point for the OIDC identity provider adapter. Registers a typed
/// <c>HttpClient</c> for token-endpoint calls, wires the OIDC configuration manager
/// (auto-refreshing JWKS via <c>{Authority}/.well-known/openid-configuration</c>),
/// and registers <see cref="OidcAgentIdentityProvider"/> as
/// <see cref="IAgentIdentityProvider"/>.
/// </summary>
public static class OidcAgentIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="OidcAgentIdentityProvider"/> as the runtime's
    /// <see cref="IAgentIdentityProvider"/>.
    /// </summary>
    /// <remarks>
    /// Wires:
    /// <list type="bullet">
    ///   <item><description><see cref="OidcAgentIdentityOptions"/> via the options pattern.</description></item>
    ///   <item><description><see cref="TimeProvider.System"/> when no <see cref="TimeProvider"/> is already registered.</description></item>
    ///   <item><description><see cref="IConfigurationManager{T}"/> for <see cref="OpenIdConnectConfiguration"/> — auto-refreshes JWKS on a 24-hour cadence.</description></item>
    ///   <item><description>A typed <see cref="System.Net.Http.HttpClient"/> for <see cref="OidcAgentIdentityProvider"/> used for <c>client_credentials</c> token requests.</description></item>
    ///   <item><description><see cref="IAgentIdentityProvider"/> resolving to the singleton <see cref="OidcAgentIdentityProvider"/>.</description></item>
    /// </list>
    /// </remarks>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">Optional options configurator.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddOidcAgentIdentity(
        this IServiceCollection services,
        Action<OidcAgentIdentityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<OidcAgentIdentityOptions>();

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OidcAgentIdentityOptions>>().Value;
            var metadataAddress = opts.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever());
        });

        services.AddHttpClient<OidcAgentIdentityProvider>();

        services.TryAddSingleton<IAgentIdentityProvider>(sp =>
            sp.GetRequiredService<OidcAgentIdentityProvider>());

        return services;
    }
}
