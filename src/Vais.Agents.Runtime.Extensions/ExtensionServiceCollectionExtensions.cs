// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Vais.Agents.Runtime.Extensions.Container;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>DI entry point for the extension loader, registry, and chain composer.</summary>
public static class ExtensionServiceCollectionExtensions
{
    /// <summary>
    /// Register the extension runtime: <see cref="ExtensionHandlerRegistry"/>,
    /// <see cref="IExtensionChainComposer"/>, <see cref="IExtensionReloader"/>,
    /// and <see cref="ExtensionManifestYamlDeserializer"/>. Safe to call multiple
    /// times — subsequent calls are no-ops because <c>TryAddSingleton</c> is idempotent.
    /// </summary>
    public static IServiceCollection AddVaisExtensions(
        this IServiceCollection services,
        ExtensionLoaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var loaderOptions = options ?? new ExtensionLoaderOptions();
        var registry = new ExtensionHandlerRegistry();

        services.TryAddSingleton(registry);
        services.TryAddSingleton<ExtensionManifestYamlDeserializer>();

        services.TryAddSingleton<IExtensionChainComposer>(sp =>
            new DefaultExtensionChainComposer(
                registry,
                sp.GetService<IAgentRegistry>()));

        services.TryAddSingleton<IExtensionReloader>(sp =>
        {
            var loaderLogger = sp.GetService<ILogger<ExtensionAssemblyLoader>>();
            var reloaderLogger = sp.GetService<ILogger<DefaultExtensionReloader>>();
            var loader = new ExtensionAssemblyLoader(sp, loaderOptions, loaderLogger);
            var composer = sp.GetRequiredService<IExtensionChainComposer>();
            var hooks = sp.GetServices<IExtensionReloadHook>();
            return new DefaultExtensionReloader(loader, registry, composer, loaderOptions, hooks, reloaderLogger);
        });

        services.TryAddSingleton<IExtensionMetricsService, InMemoryExtensionMetricsService>();
        services.TryAddSingleton<HotSeamGuard>();
        services.TryAddSingleton<IContainerExtensionHost>(NullContainerExtensionHost.Instance);
        services.TryAddSingleton<ContainerExtensionLifecycleManager>(sp =>
            new ContainerExtensionLifecycleManager(
                registry,
                sp.GetRequiredService<IExtensionChainComposer>(),
                sp.GetService<IContainerExtensionHost>(),
                sp.GetService<ILogger<ContainerExtensionLifecycleManager>>()));

        return services;
    }
}
