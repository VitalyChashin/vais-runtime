// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// Regression for the section-telemetry wiring gap: <see cref="StatefulAgentOptions.SectionTelemetrySinks"/>
/// resolved by the manifest translator must survive <see cref="AiAgentGrain"/>'s activation re-seed,
/// or declarative (grain-hosted) agents emit no per-section breakdown — which is why section tags never
/// appeared in Langfuse. Uses its own cluster (the shared fixture wires a no-sink options factory) with
/// a factory that supplies a capturing sink, then asserts the grain's agent fired it once per turn.
/// </summary>
public sealed class AiAgentGrainSectionTelemetryTests
{
    [Fact]
    public async Task SectionTelemetrySinks_Survive_Grain_Activation_And_Receive_Per_Turn_Snapshot()
    {
        CapturingSectionSink.Instance.Reset();

        var cluster = new TestClusterBuilder()
            .AddSiloBuilderConfigurator<SectionSinkSiloConfigurator>()
            .Build();
        await cluster.DeployAsync();

        var grain = cluster.GrainFactory.GetGrain<IAiAgentGrain>("grain-section-telemetry");
        try
        {
            await grain.AskAsync("hello");

            CapturingSectionSink.Instance.SnapshotCount.Should().Be(1,
                "the options-supplied section sink must survive the grain re-seed and fire exactly once per turn");
            CapturingSectionSink.Instance.LastSectionCount.Should().BeGreaterThan(0,
                "at least the base system + user-message sections are composed each turn");
        }
        finally
        {
            await grain.DeleteAsync();
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    private sealed class SectionSinkSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage(AiAgentGrain.StorageName);
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<StreamingHistorySizeProvider>();
                services.AddSingleton<ICompletionProvider>(sp => sp.GetRequiredService<StreamingHistorySizeProvider>());
                services.ConfigureAgentGrains((_, id, _) => ValueTask.FromResult(new StatefulAgentOptions
                {
                    AgentName = id,
                    SystemPrompt = "be helpful",
                    SectionTelemetrySinks = new ISectionTelemetrySink[] { CapturingSectionSink.Instance },
                }));
            });
        }
    }

    // Static singleton bridges the in-process test silo and the test assertions (TestCluster runs the
    // silo in this process, so the same instance the options factory hands the grain is observable here).
    private sealed class CapturingSectionSink : ISectionTelemetrySink
    {
        public static readonly CapturingSectionSink Instance = new();

        private int _snapshotCount;
        public int SnapshotCount => Volatile.Read(ref _snapshotCount);
        public int LastSectionCount { get; private set; }

        public void Reset()
        {
            Volatile.Write(ref _snapshotCount, 0);
            LastSectionCount = 0;
        }

        public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _snapshotCount);
            LastSectionCount = snapshot.Sections.Count;
            return ValueTask.CompletedTask;
        }
    }
}
