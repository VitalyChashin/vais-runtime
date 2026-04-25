// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// xUnit collection fixture that spins up an Orleans <see cref="TestCluster"/> with
/// memory grain storage under <see cref="AiAgentGrain.StorageName"/> plus the DI
/// registrations <see cref="AiAgentGrain"/> expects. One cluster per collection,
/// deployed once and reused across test classes.
/// </summary>
public sealed class OrleansClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        if (Cluster is not null)
        {
            await Cluster.StopAllSilosAsync();
            await Cluster.DisposeAsync();
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage(AiAgentGrain.StorageName);
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<StreamingHistorySizeProvider>();
                services.AddSingleton<ICompletionProvider>(sp => sp.GetRequiredService<StreamingHistorySizeProvider>());
                services.ConfigureAgentGrains();
            });
        }
    }
}

[CollectionDefinition(CollectionName)]
public sealed class OrleansClusterCollection : ICollectionFixture<OrleansClusterFixture>
{
    public const string CollectionName = "Orleans cluster";
}
