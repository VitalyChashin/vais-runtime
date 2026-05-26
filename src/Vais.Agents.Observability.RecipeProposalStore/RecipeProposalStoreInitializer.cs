// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.RecipeProposalStore;

/// <summary>
/// Hosted service that applies the <see cref="PostgresRecipeProposalStore"/> schema and
/// runs the retention prune on startup. Mirrors <c>InterceptorTeeStoreInitializer</c>.
/// </summary>
internal sealed class RecipeProposalStoreInitializer : IHostedService
{
    private readonly IRecipeProposalStore _store;
    private readonly RecipeProposalStoreOptions _options;

    public RecipeProposalStoreInitializer(IRecipeProposalStore store, IOptions<RecipeProposalStoreOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_store is PostgresRecipeProposalStore pg)
        {
            await pg.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.RetentionDays);
            await pg.DeleteDecidedProposalsOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
