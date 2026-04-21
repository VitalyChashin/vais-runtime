// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Runtime.Instantiation.ModelProviders;

/// <summary>
/// DI entry point for the three v0.17 built-in model-provider factories —
/// <see cref="OpenAIModelProviderFactory"/>, <see cref="AnthropicModelProviderFactory"/>,
/// <see cref="AzureOpenAIModelProviderFactory"/>. Consumers register additional
/// factories alongside; <see cref="CompletionProviderPool"/> enumerates every
/// registered <see cref="IModelProviderFactory"/> and dispatches by provider
/// string (case-insensitive).
/// </summary>
public static class BuiltinModelProvidersServiceCollectionExtensions
{
    /// <summary>
    /// Register the three built-in model-provider factories. Idempotent —
    /// calling this more than once on the same <see cref="IServiceCollection"/>
    /// adds duplicate registrations which <see cref="CompletionProviderPool"/>
    /// rejects at pool construction with an actionable error.
    /// </summary>
    public static IServiceCollection AddBuiltinModelProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IModelProviderFactory, OpenAIModelProviderFactory>();
        services.AddSingleton<IModelProviderFactory, AnthropicModelProviderFactory>();
        services.AddSingleton<IModelProviderFactory, AzureOpenAIModelProviderFactory>();

        return services;
    }
}
