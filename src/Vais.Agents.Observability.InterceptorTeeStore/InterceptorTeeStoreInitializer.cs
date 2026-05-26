// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.InterceptorTeeStore;

/// <summary>
/// Hosted service that applies the <see cref="PostgresInterceptorTeeStore"/> schema and
/// runs the retention prune on startup. Mirrors <c>AgentRunStoreInitializer</c>.
/// </summary>
internal sealed class InterceptorTeeStoreInitializer : IHostedService
{
    private readonly IInterceptorTeeStore _store;
    private readonly InterceptorTeeStoreOptions _options;

    public InterceptorTeeStoreInitializer(IInterceptorTeeStore store, IOptions<InterceptorTeeStoreOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_store is PostgresInterceptorTeeStore pg)
        {
            await pg.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.RetentionDays);
            await pg.DeleteEventsOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
