// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Vais.Agents.Eval;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// CS-11: <see cref="ContinuousScoringGrain"/> window lifecycle.
/// Samples enqueued within a window land in the same eval-run row;
/// a sample after expiry opens a new eval-run row and closes the prior one.
/// </summary>
[Collection(ContinuousScoringClusterCollection.CollectionName)]
public sealed class ContinuousScoringGrainTests
{
    private readonly ContinuousScoringClusterFixture _fx;
    public ContinuousScoringGrainTests(ContinuousScoringClusterFixture fx) => _fx = fx;

    [Fact]
    public async Task EnqueueSamples_SameWindow_LandInSameEvalRun()
    {
        var suiteId = $"suite-{Guid.NewGuid():N}";
        _fx.Registry.AddSuite(MakeSuite(suiteId, TimeSpan.FromHours(1)));

        var grain = _fx.Cluster.Client.GetGrain<IContinuousScoringGrain>(suiteId);

        var runId1 = Guid.NewGuid().ToString();
        var runId2 = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        await grain.EnqueueSampleAsync(runId1, now, "hello", null);
        await grain.EnqueueSampleAsync(runId2, now.AddMinutes(5), "world", null);

        // Allow grain turns to complete.
        await Task.Delay(500);

        var cases = _fx.ResultStore.CaseResults.Values
            .Where(c => c.EvalRunId.Contains(suiteId))
            .ToList();

        cases.Should().HaveCountGreaterOrEqualTo(2);
        var evalRunIds = cases.Select(c => c.EvalRunId).Distinct().ToList();
        evalRunIds.Should().ContainSingle(because: "both samples are in the same window");
    }

    [Fact]
    public async Task EnqueueSample_AfterWindowExpiry_OpensNewEvalRun()
    {
        var suiteId = $"suite-{Guid.NewGuid():N}";
        // Use a very short window (1 minute) and place samples 2 minutes apart.
        _fx.Registry.AddSuite(MakeSuite(suiteId, TimeSpan.FromMinutes(1)));

        var grain = _fx.Cluster.Client.GetGrain<IContinuousScoringGrain>(suiteId);

        var t0 = DateTimeOffset.UtcNow;
        await grain.EnqueueSampleAsync(Guid.NewGuid().ToString(), t0, "first", null);

        // Second sample lands 2 minutes after the first → different window.
        await grain.EnqueueSampleAsync(Guid.NewGuid().ToString(), t0.AddMinutes(2), "second", null);

        await Task.Delay(500);

        var cases = _fx.ResultStore.CaseResults.Values
            .Where(c => c.EvalRunId.Contains(suiteId))
            .ToList();

        cases.Should().HaveCountGreaterOrEqualTo(2);
        var evalRunIds = cases.Select(c => c.EvalRunId).Distinct().ToList();
        evalRunIds.Should().HaveCountGreaterOrEqualTo(2, because: "samples in different windows produce different eval-run rows");
    }

    private static EvalSuiteManifest MakeSuite(string id, TimeSpan window) =>
        new(id, "1.0")
        {
            Spec = new EvalSuiteSpec
            {
                AgentId = "test-agent",
                Sampling = new EvalSamplingSpec { Rate = 1.0, WindowDuration = window },
            },
        };
}

// ── Cluster fixture ───────────────────────────────────────────────────────────

public sealed class ContinuousScoringClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;
    public InMemoryEvalResultStore ResultStore { get; } = new();
    public InMemoryEvalSuiteRegistry Registry { get; } = new();

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        // Pass fixture references into the silo via a static slot (standard Orleans test pattern).
        SiloConfigurator.ResultStore = ResultStore;
        SiloConfigurator.Registry = Registry;
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
        // Static so they're accessible across the test process boundary.
        public static InMemoryEvalResultStore? ResultStore;
        public static InMemoryEvalSuiteRegistry? Registry;

        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage(AiAgentGrain.StorageName);
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IEvalResultStore>(_ => ResultStore!);
                services.AddSingleton<IEvalSuiteRegistry>(_ => Registry!);
            });
        }
    }
}

[CollectionDefinition(CollectionName)]
public sealed class ContinuousScoringClusterCollection : ICollectionFixture<ContinuousScoringClusterFixture>
{
    public const string CollectionName = "Continuous scoring cluster";
}

// ── Test doubles ─────────────────────────────────────────────────────────────

public sealed class InMemoryEvalResultStore : IEvalResultStore
{
    public ConcurrentDictionary<string, EvalRunSummary> Runs { get; } = new();
    public ConcurrentDictionary<string, EvalCaseResultRecord> CaseResults { get; } = new();

    public ValueTask AppendRunAsync(EvalRunSummary run, CancellationToken ct = default)
    {
        Runs[run.EvalRunId] = run;
        return ValueTask.CompletedTask;
    }

    public ValueTask AppendCaseResultAsync(EvalCaseResultRecord result, CancellationToken ct = default)
    {
        CaseResults[$"{result.EvalRunId}:{result.CaseId}"] = result;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(
        string? suiteName = null, int limit = 50, string? source = null, CancellationToken ct = default)
    {
        IEnumerable<EvalRunSummary> query = Runs.Values;
        if (suiteName is not null) query = query.Where(r => r.SuiteName == suiteName);
        if (source is not null) query = query.Where(r => r.Source == source);
        return ValueTask.FromResult<IReadOnlyList<EvalRunSummary>>(query.Take(limit).ToList());
    }

    public ValueTask<EvalRunDetail?> GetRunAsync(string evalRunId, CancellationToken ct = default)
    {
        if (!Runs.TryGetValue(evalRunId, out var run)) return ValueTask.FromResult<EvalRunDetail?>(null);
        var cases = CaseResults.Values.Where(c => c.EvalRunId == evalRunId).ToList();
        return ValueTask.FromResult<EvalRunDetail?>(new EvalRunDetail(run, cases));
    }
}

public sealed class InMemoryEvalSuiteRegistry : IEvalSuiteRegistry
{
    private readonly ConcurrentDictionary<string, EvalSuiteManifest> _suites = new();

    public void AddSuite(EvalSuiteManifest suite) => _suites[suite.Id] = suite;

    public async IAsyncEnumerable<EvalSuiteManifest> ListAsync(
        string? labelPrefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var s in _suites.Values)
        {
            ct.ThrowIfCancellationRequested();
            yield return s;
        }
        await Task.CompletedTask;
    }

    public ValueTask<EvalSuiteManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default) =>
        ValueTask.FromResult(_suites.TryGetValue(id, out var s) ? s : null);

    public ValueTask UpsertAsync(EvalSuiteManifest manifest, CancellationToken ct = default)
    {
        _suites[manifest.Id] = manifest;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default)
    {
        _suites.TryRemove(id, out _);
        return ValueTask.CompletedTask;
    }
}
