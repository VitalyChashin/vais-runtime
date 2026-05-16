// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Core;

/// <summary>
/// Default <see cref="IAgentInputMiddlewareFactory"/> that resolves middleware by name from the
/// collection of <see cref="NamedAgentInputMiddlewareRegistration"/> singletons registered in DI.
/// Phase-2 cognitive primitive packages contribute registrations via
/// <c>AddNamedAgentInputMiddleware</c> extension methods.
/// </summary>
public sealed class DefaultAgentInputMiddlewareFactory : IAgentInputMiddlewareFactory
{
    private readonly Dictionary<string, NamedAgentInputMiddlewareRegistration> _map;
    private readonly IServiceProvider _services;

    /// <param name="registrations">All named registrations contributed by Phase-2 packages.</param>
    /// <param name="services">Service provider forwarded to each factory lambda.</param>
    public DefaultAgentInputMiddlewareFactory(
        IEnumerable<NamedAgentInputMiddlewareRegistration> registrations,
        IServiceProvider services)
    {
        _map = registrations.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        _services = services;
    }

    /// <inheritdoc/>
    public AgentInputMiddleware Create(GatewayMiddlewareSpec spec)
    {
        if (!_map.TryGetValue(spec.Name, out var reg))
            throw new InvalidOperationException(
                $"No agent input middleware named '{spec.Name}' is registered. " +
                $"Known names: {string.Join(", ", _map.Keys)}.");

        return reg.Factory(spec, _services);
    }
}
