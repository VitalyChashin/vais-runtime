// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Core;

/// <summary>
/// <see cref="IInboundIdentityResolver"/> that accepts any bearer token and
/// returns a fixed <see cref="AgentContext"/>. Intended for single-tenant embedded
/// deployments where the caller is already trusted.
/// </summary>
/// <remarks>
/// <b>Do not use in production multi-tenant deployments.</b> This resolver
/// performs no token validation; any caller with network access can present any
/// token and receive the configured context. Use a proprietary module that
/// validates tokens against a key store instead.
/// </remarks>
public sealed class PassThroughIdentityResolver : IInboundIdentityResolver
{
    private readonly AgentContext _context;

    /// <summary>
    /// Initializes the resolver with an optional fixed <paramref name="context"/>.
    /// Logs a warning to <paramref name="logger"/> to prevent silent use in production.
    /// </summary>
    public PassThroughIdentityResolver(
        ILogger<PassThroughIdentityResolver> logger,
        AgentContext? context = null)
    {
        _context = context ?? AgentContext.Empty;
        logger.LogWarning(
            "PassThroughIdentityResolver is active: all bearer tokens are accepted without " +
            "validation. Do not use in production multi-tenant deployments.");
    }

    /// <inheritdoc/>
    public ValueTask<AgentContext> ResolveAsync(string bearerToken, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_context);
}

public static partial class LlmGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PassThroughIdentityResolver"/> as <see cref="IInboundIdentityResolver"/>.
    /// Accepts any bearer token and returns <paramref name="context"/> (or
    /// <see cref="AgentContext.Empty"/> when null). For single-tenant / development use only.
    /// </summary>
    public static IServiceCollection AddPassThroughIdentityResolver(
        this IServiceCollection services,
        AgentContext? context = null)
    {
        services.AddSingleton<IInboundIdentityResolver>(sp =>
            new PassThroughIdentityResolver(
                sp.GetRequiredService<ILogger<PassThroughIdentityResolver>>(),
                context));
        return services;
    }
}
