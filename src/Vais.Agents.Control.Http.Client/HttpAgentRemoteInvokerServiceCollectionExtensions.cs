// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Control.Http;

/// <summary>DI registration helpers for <see cref="IAgentRemoteInvoker"/>.</summary>
public static class HttpAgentRemoteInvokerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAgentRemoteInvoker"/> as a singleton backed by
    /// <see cref="HttpAgentRemoteInvoker"/>. Uses <see cref="IHttpClientFactory"/>
    /// when available in the container; falls back to direct <see cref="System.Net.Http.HttpClient"/> construction.
    /// </summary>
    public static IServiceCollection AddAgentRemoteInvoker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAgentRemoteInvoker>(sp =>
            new HttpAgentRemoteInvoker(sp.GetService<IHttpClientFactory>()));
        return services;
    }
}
