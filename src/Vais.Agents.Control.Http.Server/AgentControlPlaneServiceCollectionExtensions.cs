// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Control.Http;

/// <summary>
/// DI convenience for mounting the control-plane HTTP surface. Registers the
/// manifest loader default (<see cref="JsonAgentManifestLoader"/>) the Create
/// and Update endpoints deserialise request bodies through. Consumers supply
/// <see cref="IAgentLifecycleManager"/> + <see cref="IAgentRegistry"/> themselves
/// via their preferred wiring (<c>AddInProcessLifecycleManager()</c> from
/// <c>Vais.Agents.Control.InProcess</c>, Orleans-backed equivalents, etc).
/// </summary>
public static class AgentControlPlaneServiceCollectionExtensions
{
    /// <summary>Register the pieces the routes need that aren't already in DI.</summary>
    public static IServiceCollection AddAgentControlPlane(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAgentManifestLoader, JsonAgentManifestLoader>();
        services.TryAddSingleton<JsonAgentGraphManifestLoader>();

        // Register the capturing sink as both the concrete type (HTTP handlers resolve
        // it directly to call BeginCapture) and the interface (the manifest translator
        // resolves IManifestApplyDiagnosticsSink from DI at construction time).
        // AddSingleton (not TryAdd) so the HTTP layer always owns the interface binding.
        // Hosts wishing to fan-out to a custom sink should wrap this instance.
        services.TryAddSingleton<CapturingManifestApplyDiagnosticsSink>();
        services.AddSingleton<IManifestApplyDiagnosticsSink>(
            sp => sp.GetRequiredService<CapturingManifestApplyDiagnosticsSink>());

        return services;
    }

    /// <summary>
    /// Register the in-process <see cref="IIdempotencyStore"/> default
    /// (<see cref="InMemoryIdempotencyStore"/>) + configure <see cref="IdempotencyOptions"/>.
    /// Consumers who want durable dedupe across silo restart call
    /// <c>services.AddOrleansIdempotencyStore()</c> from <c>Vais.Agents.Hosting.Orleans</c>
    /// <b>before</b> this extension — <c>TryAddSingleton</c> discipline means the
    /// first registered store wins.
    /// </summary>
    public static IServiceCollection AddAgentControlPlaneIdempotency(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddOptions<IdempotencyOptions>();
        if (configure is not null)
        {
            builder.Configure(configure);
        }
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return services;
    }
}
