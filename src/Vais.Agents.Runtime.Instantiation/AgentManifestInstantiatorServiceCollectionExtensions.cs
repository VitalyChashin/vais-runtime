// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// DI entry points for the manifest-driven agent instantiator (v0.17 Pillar B).
/// </summary>
/// <remarks>
/// <para>
/// Wire order matches the Pillar A footgun pattern — translator + provider pool
/// + static tool registry + prompt registries all register as singletons, then
/// the consumer wires <c>ConfigureAgentGrains</c> with a lambda that resolves
/// the translator. The Pillar A composition-root unit tests keep the ordering
/// honest; the Pillar B test set adds <c>Composition_Translator_Registered_Before_ConfigureAgentGrains</c>.
/// </para>
/// <para>
/// <b>Provider + guardrail factories</b> come from <c>AddBuiltinModelProviders</c>
/// (PR 2) and <c>AddBuiltinGuardrails</c> (PR 2). This extension assumes both
/// have run and at least one of each is registered; the translator throws at
/// translate time if a manifest references an unknown provider or guardrail name.
/// </para>
/// </remarks>
public static class AgentManifestInstantiatorServiceCollectionExtensions
{
    /// <summary>
    /// Register the manifest translator + completion provider pool as
    /// singletons. Caller owns the registration of
    /// <see cref="IModelProviderFactory"/> and <see cref="IGuardrailFactory"/>
    /// implementations separately — typically via <c>AddBuiltinModelProviders</c>
    /// and <c>AddBuiltinGuardrails</c>.
    /// </summary>
    public static IServiceCollection AddAgentManifestInstantiator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICompletionProviderPool>(sp =>
            new CompletionProviderPool(
                sp.GetServices<IModelProviderFactory>(),
                sp.GetRequiredService<Vais.Agents.Control.ISecretResolver>()));

        services.TryAddSingleton<IAgentManifestTranslator>(sp => new AgentManifestTranslator(
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<ICompletionProviderPool>(),
            sp.GetServices<IGuardrailFactory>(),
            sp,
            sp.GetService<IStaticToolRegistry>(),
            sp.GetService<IPromptTemplateRegistry>(),
            sp.GetService<IPromptFileLoader>(),
            sp.GetService<Vais.Agents.Runtime.Plugins.IPluginHandlerRegistry>(),
            sp.GetService<Vais.Agents.Control.IManifestApplyDiagnosticsSink>(),
            sp.GetServices<INamedToolSourceProvider>(),
            sp.GetService<ILlmGatewayConfigRegistry>(),
            sp.GetService<IMcpGatewayConfigRegistry>(),
            sp.GetService<IMcpServerRegistry>(),
            sp.GetService<ILlmGatewayMiddlewareFactory>(),
            sp.GetService<IToolGatewayMiddlewareFactory>()));

        // Alias — AgentLifecycleManager (Control.InProcess) depends on the narrower
        // IAgentManifestInvalidator contract; point it at the translator singleton
        // so eviction on UpdateAsync / EvictAsync flows through the same cache.
        services.TryAddSingleton<Vais.Agents.Control.IAgentManifestInvalidator>(sp =>
            sp.GetRequiredService<IAgentManifestTranslator>());

        // v0.22 Pillar F — after a plugin hot-reload, invalidate translator cache entries
        // for every agent whose handler type name belongs to the swapped plugin. Registered
        // as IPluginReloadHook so DefaultPluginReloader.DispatchHooksAsync picks it up.
        // AddSingleton (not TryAdd) — multiple hooks of the same type are allowed.
        services.AddSingleton<Vais.Agents.Runtime.Plugins.IPluginReloadHook>(sp =>
            new TranslatorInvalidationHook(
                sp.GetRequiredService<IAgentRegistry>(),
                sp.GetRequiredService<IAgentManifestTranslator>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<TranslatorInvalidationHook>>()));

        return services;
    }

    /// <summary>
    /// Register an <see cref="IStaticToolRegistry"/> built from the supplied
    /// builder delegate. Only one registry may be registered per DI container —
    /// a second call throws.
    /// </summary>
    public static IServiceCollection AddStaticToolRegistry(
        this IServiceCollection services,
        Action<IStaticToolRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new StaticToolRegistryBuilder();
        configure(builder);

        services.AddSingleton<IStaticToolRegistry>(_ => builder.Build());
        return services;
    }

    /// <summary>
    /// Register an <see cref="IPromptTemplateRegistry"/> built from the
    /// supplied builder delegate. Only one registry may be registered per DI
    /// container.
    /// </summary>
    public static IServiceCollection AddPromptTemplateRegistry(
        this IServiceCollection services,
        Action<IPromptTemplateRegistryBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new PromptTemplateRegistryBuilder();
        configure(builder);

        services.AddSingleton<IPromptTemplateRegistry>(_ => builder.Build());
        return services;
    }

    /// <summary>
    /// Register the filesystem-backed <see cref="IPromptFileLoader"/> rooted at
    /// <paramref name="rootPath"/>. Replaces any prior registration.
    /// </summary>
    public static IServiceCollection AddFileSystemPromptFileLoader(this IServiceCollection services, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        services.AddSingleton<IPromptFileLoader>(_ => new FileSystemPromptFileLoader(rootPath));
        return services;
    }
}
