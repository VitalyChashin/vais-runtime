// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Default <see cref="ILlmGatewayMiddlewareFactory"/> that resolves middleware by name from the
/// collection of <see cref="NamedLlmGatewayMiddlewareRegistration"/> singletons registered in DI.
/// Each <c>Vais.Agents.Gateways.*</c> package contributes registrations via
/// <c>AddNamedLlmGatewayMiddleware_*</c> extension methods.
/// </summary>
public sealed class DefaultLlmGatewayMiddlewareFactory : ILlmGatewayMiddlewareFactory
{
    private readonly Dictionary<string, NamedLlmGatewayMiddlewareRegistration> _map;
    private readonly IServiceProvider _services;

    /// <param name="registrations">All named registrations contributed by gateway packages.</param>
    /// <param name="services">Service provider forwarded to each factory lambda.</param>
    public DefaultLlmGatewayMiddlewareFactory(
        IEnumerable<NamedLlmGatewayMiddlewareRegistration> registrations,
        IServiceProvider services)
    {
        _map = registrations.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        _services = services;
    }

    /// <inheritdoc/>
    public LlmGatewayMiddleware Create(GatewayMiddlewareSpec spec)
    {
        if (!_map.TryGetValue(spec.Name, out var reg))
            throw new InvalidOperationException(
                $"No LLM gateway middleware named '{spec.Name}' is registered. " +
                $"Known names: {string.Join(", ", _map.Keys)}.");

        return reg.Factory(spec, _services);
    }
}
