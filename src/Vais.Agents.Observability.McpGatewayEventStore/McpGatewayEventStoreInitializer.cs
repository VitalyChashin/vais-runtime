// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.McpGatewayEventStore;

internal sealed class McpGatewayEventStoreInitializer : IHostedService
{
    private readonly IMcpGatewayEventStore _store;
    private readonly McpGatewayEventStoreOptions _options;

    public McpGatewayEventStoreInitializer(IMcpGatewayEventStore store, IOptions<McpGatewayEventStoreOptions> options)
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
