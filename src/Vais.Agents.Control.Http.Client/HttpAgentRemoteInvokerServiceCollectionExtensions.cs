// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Control.Http;

/// <summary>DI registration helpers for <see cref="IAgentRemoteInvoker"/>.</summary>
public static class HttpAgentRemoteInvokerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAgentRemoteInvoker"/> as a singleton backed by
    /// <see cref="HttpAgentRemoteInvoker"/>. Uses <see cref="IHttpClientFactory"/>
    /// when available in the container; falls back to direct <see cref="System.Net.Http.HttpClient"/> construction.
    /// Bearer tokens are forwarded verbatim (v0.20 behaviour).
    /// </summary>
    public static IServiceCollection AddAgentRemoteInvoker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAgentRemoteInvoker>(sp =>
            new HttpAgentRemoteInvoker(sp.GetService<IHttpClientFactory>()));
        return services;
    }

    /// <summary>
    /// Registers <see cref="IAgentRemoteInvoker"/> with configurable per-runtime identity propagation.
    /// For each entry in <paramref name="configure"/>, the appropriate
    /// <see cref="IRemoteIdentityProvider"/> is created. Runtimes not listed default to
    /// <see cref="RemoteIdentityMode.Forward"/> (bearer-token pass-through).
    /// </summary>
    public static IServiceCollection AddAgentRemoteInvoker(
        this IServiceCollection services,
        Action<RemoteRuntimeOptionsMap> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var map = new RemoteRuntimeOptionsMap();
        configure(map);

        // Validate all entries.
        foreach (var (url, opts) in map.Runtimes)
            opts.Validate();

        var forwarding = new ForwardingRemoteIdentityProvider();
        var providers = new Dictionary<string, IRemoteIdentityProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var (url, opts) in map.Runtimes)
        {
            providers[url.TrimEnd('/')] = opts.IdentityMode switch
            {
                RemoteIdentityMode.ServiceAccount => new ServiceAccountRemoteIdentityProvider(
                    opts.ServiceAccountTokenPath, TimeProvider.System, opts.TokenCacheTtl),
                RemoteIdentityMode.TokenExchange => CreateTokenExchangeProvider(services, opts),
                _ => forwarding,
            };
        }

        var composite = new CompositeRemoteIdentityProvider(providers, forwarding);

        services.TryAddSingleton<IAgentRemoteInvoker>(sp =>
            new HttpAgentRemoteInvoker(
                sp.GetService<IHttpClientFactory>(),
                identityProvider: composite,
                optionsLookup: url => map.Runtimes.TryGetValue(url, out var o) ? o : null));

        return services;
    }

    private static OidcTokenExchangeRemoteIdentityProvider CreateTokenExchangeProvider(
        IServiceCollection services,
        RemoteRuntimeOptions opts)
    {
        // Resolve ISecretResolver from the container at build time is not possible,
        // so we create a temporary service provider for this singleton.
        // This is acceptable because these are singleton registrations.
        var tempProvider = services.BuildServiceProvider();
        var secretResolver = tempProvider.GetRequiredService<ISecretResolver>();
        var logger = tempProvider.GetService<ILogger<OidcTokenExchangeRemoteIdentityProvider>>();

        var httpClient = new HttpClient { BaseAddress = opts.TokenExchangeEndpoint };

        return new OidcTokenExchangeRemoteIdentityProvider(
            httpClient, secretResolver, opts, TimeProvider.System, logger);
    }
}
