// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Orleans.Runtime;
using Vais2.Agents.Hosting.Orleans;
using Xunit;

namespace Vais2.Agents.CrossHostTests;

/// <summary>
/// The canonical parity scenario: run the same deterministic turn sequence against
/// <see cref="Vais2.Agents.Hosting.InMemory.InMemoryAgentRuntime"/> and two Orleans hosts (Redis-backed +
/// Postgres-backed), snapshot what each produced (history + filter invocations +
/// usage records), and assert the snapshots are byte-for-byte equal.
/// </summary>
/// <remarks>
/// <para>
/// Why this matters: the three hosts wire <see cref="Vais2.Agents.Core.StatefulAiAgent"/> through
/// completely different plumbing. InMemory is a plain object graph. Orleans wraps
/// the same agent in <see cref="Vais2.Agents.Hosting.Orleans.AiAgentGrain"/> and shuttles state through
/// <see cref="Orleans.Runtime.IPersistentState{TState}"/> — with Redis and Postgres
/// implementations that serialise differently. "Same inputs, same observable
/// outputs" is the property a consumer needs to trust before swapping hosts in
/// production, and it's surprisingly easy to quietly violate (e.g. grain
/// deactivation between turns changing history ordering, persistence providers
/// normalising nulls, filter pipeline being wired differently on the silo side).
/// </para>
/// </remarks>
[Collection(CrossHostCollection.CollectionName)]
public sealed class ParityScenarioTests
{
    private readonly CrossHostFixture _fx;

    public ParityScenarioTests(CrossHostFixture fx) => _fx = fx;

    [Fact]
    public async Task Three_Turn_Scenario_Produces_Identical_Snapshots_On_All_Three_Hosts()
    {
        _fx.ClearRecordings();

        const string systemPrompt = "be-concise";
        var replies = new Dictionary<string, IReadOnlyList<string>>();
        var histories = new Dictionary<string, IReadOnlyList<string>>();

        foreach (var (host, runtime, sink, filter) in Hosts())
        {
            var agent = runtime.GetOrCreate($"parity-{host}");
            try
            {
                agent.SystemPrompt = systemPrompt;

                var hostReplies = new List<string>
                {
                    await agent.AskAsync("turn-1"),
                    await agent.AskAsync("turn-2"),
                    await agent.AskAsync("turn-3"),
                };
                replies[host] = hostReplies;
                histories[host] = ParitySnapshot.SummariseHistory(agent.History);
            }
            finally
            {
                runtime.Remove($"parity-{host}");
            }
        }

        // Every host must have produced the same replies (the deterministic provider
        // echoes history size, so history-size=1,3,5 is what all three should see).
        replies["InMemory"].Should().Equal("history-size=1", "history-size=3", "history-size=5");
        replies["Redis"].Should().Equal(replies["InMemory"]);
        replies["Postgres"].Should().Equal(replies["InMemory"]);

        // History shape equal across hosts.
        histories["Redis"].Should().Equal(histories["InMemory"]);
        histories["Postgres"].Should().Equal(histories["InMemory"]);

        // Filter invocations (history size + system prompt) equal across hosts.
        var baseline = _fx.InMemoryFilter.Invocations;
        baseline.Should().Equal(
            $"history=1,prompt={systemPrompt}",
            $"history=3,prompt={systemPrompt}",
            $"history=5,prompt={systemPrompt}");
        _fx.RedisFilter.Invocations.Should().Equal(baseline);
        _fx.PostgresFilter.Invocations.Should().Equal(baseline);

        // Usage summaries equal across hosts (summaries exclude Duration/StartedAt).
        var inMemoryUsage = _fx.InMemorySink.Records.Select(ParitySnapshot.SummariseUsage).ToArray();
        inMemoryUsage.Should().HaveCount(3);
        _fx.RedisSink.Records.Select(ParitySnapshot.SummariseUsage).Should().Equal(inMemoryUsage);
        _fx.PostgresSink.Records.Select(ParitySnapshot.SummariseUsage).Should().Equal(inMemoryUsage);
    }

    [Fact]
    public async Task Scenario_Interrupted_By_Grain_Deactivation_Still_Matches_InMemory_Baseline_On_Next_Turn()
    {
        _fx.ClearRecordings();

        // 1. Drive InMemory through four turns end-to-end — this is the baseline
        //    we expect the Orleans hosts to match even when their grains are evicted
        //    between turn 2 and turn 3.
        var inMemoryAgent = _fx.InMemoryRuntime.GetOrCreate("parity-rehydrate-inmemory");
        try
        {
            await inMemoryAgent.AskAsync("q1");
            await inMemoryAgent.AskAsync("q2");
            await inMemoryAgent.AskAsync("q3");
            await inMemoryAgent.AskAsync("q4");
        }
        finally
        {
            _fx.InMemoryRuntime.Remove("parity-rehydrate-inmemory");
        }

        var baselineHistory = ParitySnapshot.SummariseHistory(inMemoryAgent.History);
        baselineHistory.Should().HaveCount(8);

        // 2. For each Orleans host, drive two turns, force activation collection, drive
        //    two more — against a FRESH runtime+proxy so the client cache can't
        //    accidentally paper over a rehydration bug. If rehydration is correct the
        //    final history should match InMemory.
        foreach (var (host, cluster) in OrleansHosts())
        {
            var grainId = $"parity-rehydrate-{host.ToLowerInvariant()}";
            var firstRuntime = new OrleansAgentRuntime(cluster.GrainFactory);
            var before = firstRuntime.GetOrCreate(grainId);
            try
            {
                await before.AskAsync("q1");
                await before.AskAsync("q2");

                // Evict all activations; the next grain call reactivates and rehydrates.
                var management = cluster.GrainFactory.GetGrain<IManagementGrain>(0);
                await management.ForceActivationCollection(TimeSpan.Zero);

                // Fresh runtime → fresh proxy → cold cache → forces a re-read from storage.
                var secondRuntime = new OrleansAgentRuntime(cluster.GrainFactory);
                var after = secondRuntime.GetOrCreate(grainId);
                await after.AskAsync("q3");
                await after.AskAsync("q4");

                var rehydratedHistory = ParitySnapshot.SummariseHistory(after.History);
                rehydratedHistory.Should().Equal(baselineHistory, $"{host} rehydration should preserve all 8 turns");
            }
            finally
            {
                firstRuntime.Remove(grainId);
            }
        }
    }

    private IEnumerable<(string Host, IAgentRuntime Runtime, RecordingUsageSink Sink, RecordingFilter Filter)> Hosts()
    {
        yield return ("InMemory", _fx.InMemoryRuntime, _fx.InMemorySink, _fx.InMemoryFilter);
        yield return ("Redis", _fx.RedisRuntime, _fx.RedisSink, _fx.RedisFilter);
        yield return ("Postgres", _fx.PostgresRuntime, _fx.PostgresSink, _fx.PostgresFilter);
    }

    private IEnumerable<(string Host, Orleans.TestingHost.TestCluster Cluster)> OrleansHosts()
    {
        yield return ("Redis", _fx.RedisCluster);
        yield return ("Postgres", _fx.PostgresCluster);
    }
}
