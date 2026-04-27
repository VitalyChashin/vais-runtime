// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Core;

/// <summary>
/// <see cref="IModelRouter"/> backed by an in-memory alias dictionary.
/// Alias lookup is case-insensitive. For deployments that need dynamic routing
/// (Redis-backed, capability-weighted, multi-tenant) implement a custom
/// <see cref="IModelRouter"/> in a proprietary module.
/// </summary>
public sealed class InMemoryModelRouter : IModelRouter
{
    private readonly IReadOnlyDictionary<string, ModelRoute> _routes;
    private readonly IReadOnlyList<string> _aliases;

    /// <summary>Initializes the router with a pre-built alias-to-route map.</summary>
    public InMemoryModelRouter(IReadOnlyDictionary<string, ModelRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        _routes = routes;
        _aliases = [.. routes.Keys];
    }

    /// <inheritdoc/>
    public ValueTask<ModelRoute> ResolveAsync(string modelAlias, CancellationToken cancellationToken = default)
    {
        if (_routes.TryGetValue(modelAlias, out var route))
            return ValueTask.FromResult(route);

        throw new ModelNotFoundException(modelAlias);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAliasesAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_aliases);
}

public static partial class LlmGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="InMemoryModelRouter"/> as <see cref="IModelRouter"/> with
    /// pre-built routes. Alias lookup is case-insensitive.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddInMemoryModelRouter(routes =>
    /// {
    ///     routes.Add("gpt-4o", new ModelRoute(openAiProvider, new ModelSpec("openai", "gpt-4o")));
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddInMemoryModelRouter(
        this IServiceCollection services,
        Action<Dictionary<string, ModelRoute>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var routes = new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase);
        configure(routes);
        services.AddSingleton<IModelRouter>(new InMemoryModelRouter(routes));
        return services;
    }
}
