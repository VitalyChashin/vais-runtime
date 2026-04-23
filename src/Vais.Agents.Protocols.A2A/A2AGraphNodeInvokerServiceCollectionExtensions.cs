// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Protocols.A2A;

/// <summary>DI registration helpers for <see cref="IA2AGraphNodeInvoker"/>.</summary>
public static class A2AGraphNodeInvokerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IA2AGraphNodeInvoker"/> as a singleton backed by
    /// <see cref="A2AGraphNodeInvoker"/>. Each invocation creates a fresh
    /// <see cref="HttpClient"/> to avoid stale bearer tokens.
    /// </summary>
    public static IServiceCollection AddA2AGraphNodeInvoker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IA2AGraphNodeInvoker, A2AGraphNodeInvoker>();
        return services;
    }
}
