// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;
using Vais.Agents.Runtime.Instantiation;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// The set of unconditionally-registered runtime services that MUST be present for the host
/// to function, plus a <see cref="Verify"/> probe that checks each is registered at startup.
/// </summary>
/// <remarks>
/// <para>
/// A missing registration here used to surface as a <c>ManifestInstantiationException</c> at the
/// first invoke of an affected agent (the recurring "Shape B" failure) rather than at boot.
/// <c>ValidateOnBuild</c> validates constructor dependencies, but several of these contracts are
/// resolved via service location <em>during manifest translation / grain activation</em>
/// (e.g. <see cref="IAgentRuntime"/>, <see cref="IBackgroundAgentTracker"/>) — invisible to
/// <c>ValidateOnBuild</c>. Listing them here and checking each is registered at startup closes
/// that gap. The probe checks <em>registration</em> only (via <c>IServiceProviderIsService</c>);
/// it deliberately does not construct, so it has no side effects (no plugin loading) and never
/// turns lazy, environment-dependent construction-time validation into a boot failure.
/// </para>
/// <para>
/// Only ALWAYS-ON contracts belong here. Conditionally-gated services (<c>IIdempotencyStore</c>,
/// <c>IPluginHandlerRegistry</c>, JWT auth types, the Postgres event/run stores) are covered by
/// their own dedicated composition-root tests, not this list.
/// </para>
/// </remarks>
internal static class CriticalRuntimeContracts
{
    /// <summary>Always-registered contracts the host cannot run without.</summary>
    public static readonly IReadOnlyList<Type> All =
    [
        // Core runtime
        typeof(IAgentRuntime),
        typeof(IAgentEventBus),
        typeof(IAgentContextAccessor),
        typeof(IBackgroundAgentTracker),
        // Durable registries
        typeof(IAgentRegistry),
        typeof(IAgentGraphRegistry),
        typeof(ILlmGatewayConfigRegistry),
        typeof(IMcpGatewayConfigRegistry),
        typeof(IMcpServerRegistry),
        // Manifest pipeline
        typeof(ICompletionProviderPool),
        typeof(IAgentManifestTranslator),
        typeof(IAgentManifestInvalidator),
        typeof(ISecretResolver),
        // Gateway middleware factories
        typeof(ILlmGatewayMiddlewareFactory),
        typeof(IToolGatewayMiddlewareFactory),
        // Lifecycle managers
        typeof(IAgentLifecycleManager),
        typeof(IAgentGraphLifecycleManager),
        typeof(ILlmGatewayConfigLifecycleManager),
        typeof(IMcpGatewayConfigLifecycleManager),
        typeof(IMcpServerLifecycleManager),
    ];

    /// <summary>
    /// Check every contract in <see cref="All"/> is registered, collecting all that are missing,
    /// and throw a single aggregate <see cref="InvalidOperationException"/> if any are. Uses
    /// <see cref="IServiceProviderIsService"/> so nothing is constructed. Call once at startup
    /// against the built root provider so omissions fail the boot, not the first request.
    /// </summary>
    public static void Verify(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var isService = services.GetService<IServiceProviderIsService>();

        var missing = new List<string>();
        foreach (var contract in All)
        {
            // Registration check only — never resolve/construct (see remarks on the class).
            var registered = isService is not null
                ? isService.IsService(contract)
                : services.GetService(contract) is not null;
            if (!registered)
            {
                missing.Add(contract.FullName ?? contract.Name);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Runtime startup self-check failed — the following critical services are not "
                + "registered (a registration is likely missing from CompositionRoot.ConfigureServices):"
                + Environment.NewLine
                + string.Join(Environment.NewLine, missing.Select(m => "  - " + m)));
        }
    }
}
