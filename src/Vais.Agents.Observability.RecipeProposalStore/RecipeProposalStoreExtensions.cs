// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.RecipeProposalStore;

/// <summary>DI entry points for the Postgres-backed recipe proposal store.</summary>
public static class RecipeProposalStoreExtensions
{
    /// <summary>
    /// Register <see cref="PostgresRecipeProposalStore"/> as the singleton
    /// <see cref="IRecipeProposalStore"/> and an <see cref="IHostedService"/> that applies the
    /// schema + runs the retention prune at startup. Replaces any prior
    /// <see cref="IRecipeProposalStore"/> registration (the runtime's in-memory default).
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="configure">Configure connection string + retention.</param>
    /// <param name="highRiskApprovalCheckFactory">
    /// Optional factory that returns the gate invoked before flipping a high-risk proposal to
    /// Approved. The factory is invoked at resolution time with the full <see cref="IServiceProvider"/>
    /// so it can capture <c>IApprovalStore</c> or any other dependency. The gate must throw
    /// <c>ApprovalRequiredException</c> when no matching approval exists. Pass <c>null</c> for
    /// no gate (low-/medium-/high-risk all flip directly — single-operator deployments).
    /// </param>
    public static IServiceCollection AddPostgresRecipeProposalStore(
        this IServiceCollection services,
        Action<RecipeProposalStoreOptions> configure,
        Func<IServiceProvider, Func<RecipeProposal, string, CancellationToken, ValueTask>?>? highRiskApprovalCheckFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);

        services.AddSingleton<IRecipeProposalStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<RecipeProposalStoreOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<PostgresRecipeProposalStore>>();
            var gate = highRiskApprovalCheckFactory?.Invoke(sp);
            return new PostgresRecipeProposalStore(opts.ConnectionString, logger, gate);
        });

        services.AddHostedService<RecipeProposalStoreInitializer>();
        return services;
    }
}
