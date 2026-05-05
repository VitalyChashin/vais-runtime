// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Vais.Agents.Observability.AgentRunStore;

internal sealed class AgentRunStoreInitializer : IHostedService
{
    private readonly IAgentRunStore _store;
    private readonly AgentRunStoreOptions _options;

    public AgentRunStoreInitializer(IAgentRunStore store, IOptions<AgentRunStoreOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(_options.RetentionDays);
        await _store.DeleteRunsOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
