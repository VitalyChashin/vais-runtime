// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vais.Agents.Core;

namespace Vais.Agents.Gateways.OpenAiCompat;

/// <summary>
/// Extension methods for registering the OpenAI-compatible gateway services.
/// </summary>
public static class OpenAiCompatServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core services required by the OpenAI-compatible gateway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers must register the following additional services before calling
    /// <see cref="OpenAiCompatEndpoints.MapOpenAiCompat"/>:
    /// <list type="bullet">
    ///   <item><see cref="IInboundIdentityResolver"/> — use <c>AddPassThroughIdentityResolver()</c>
    ///   for single-tenant / development, or a proprietary implementation for multi-tenant.</item>
    ///   <item><see cref="IModelRouter"/> — use <c>AddInMemoryModelRouter()</c> or a custom implementation.</item>
    /// </list>
    /// </para>
    /// <para>
    /// An <see cref="AsyncLocalAgentContextAccessor"/> is registered as a singleton under both
    /// <see cref="IAgentContextAccessor"/> and <see cref="IAgentContextSetter"/>, so that the
    /// endpoint can push the resolved context and downstream middleware can read it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddOpenAiCompatGateway(this IServiceCollection services)
    {
        // Register the shared context accessor under both read and write interfaces.
        services.TryAddSingleton<AsyncLocalAgentContextAccessor>();
        services.TryAddSingleton<IAgentContextAccessor>(sp =>
            sp.GetRequiredService<AsyncLocalAgentContextAccessor>());
        services.TryAddSingleton<IAgentContextSetter>(sp =>
            sp.GetRequiredService<AsyncLocalAgentContextAccessor>());

        return services;
    }
}
