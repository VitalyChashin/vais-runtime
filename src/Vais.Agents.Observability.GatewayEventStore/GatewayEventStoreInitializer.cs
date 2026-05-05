// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.GatewayEventStore;

internal sealed class GatewayEventStoreInitializer : IHostedService
{
    private readonly IGatewayEventStore _store;
    private readonly GatewayEventStoreOptions _options;

    public GatewayEventStoreInitializer(IGatewayEventStore store, IOptions<GatewayEventStoreOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.RetentionDays);
        await _store.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
