// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Eval.Continuous;
using Xunit;

namespace Vais.Agents.Hosting.Orleans.Tests;

/// <summary>
/// CS-11: <see cref="ContinuousSuiteActivator"/> refreshes <see cref="IContinuousSuiteIndex"/>
/// from <see cref="IEvalSuiteRegistry"/> on startup so sampling decisions reflect
/// the registered suites within milliseconds of <c>StartAsync</c>.
/// </summary>
public sealed class ContinuousSuiteActivatorTests
{
    [Fact]
    public async Task StartAsync_PopulatesIndex_FromRegistry()
    {
        var suite = MakeSuite("suite-1", "agent-x", rate: 0.1);
        var registry = new StubRegistry([suite]);
        var index = new InMemoryContinuousSuiteIndex();

        var activator = new ContinuousSuiteActivator(registry, index, NullLogger<ContinuousSuiteActivator>.Instance);
        await activator.StartAsync(default);
        await activator.StopAsync(default);

        var suites = index.SuitesFor("ws-1", "agent-x", null).ToList();
        suites.Should().ContainSingle(e => e.SuiteId == "suite-1");
    }

    [Fact]
    public async Task StartAsync_EmptyRegistry_IndexIsEmpty()
    {
        var registry = new StubRegistry([]);
        var index = new InMemoryContinuousSuiteIndex();

        var activator = new ContinuousSuiteActivator(registry, index, NullLogger<ContinuousSuiteActivator>.Instance);
        await activator.StartAsync(default);
        await activator.StopAsync(default);

        index.SuitesFor("ws", "any", null).Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_ReplacesIndex_WhenSuiteListChanges()
    {
        var suite1 = MakeSuite("s1", "agent-a", 0.5);
        var suite2 = MakeSuite("s2", "agent-b", 0.2);
        var registry = new StubRegistry([suite1, suite2]);
        var index = new InMemoryContinuousSuiteIndex();

        var activator = new ContinuousSuiteActivator(registry, index, NullLogger<ContinuousSuiteActivator>.Instance);
        await activator.StartAsync(default);
        await activator.StopAsync(default);

        index.SuitesFor("", "agent-a", null).Should().ContainSingle();
        index.SuitesFor("", "agent-b", null).Should().ContainSingle();

        // Simulate registry change.
        registry.Suites = [suite2];
        await activator.StartAsync(default);
        await activator.StopAsync(default);

        index.SuitesFor("", "agent-a", null).Should().BeEmpty();
        index.SuitesFor("", "agent-b", null).Should().ContainSingle();
    }

    private static EvalSuiteManifest MakeSuite(string id, string agentRef, double rate) =>
        new(id, "1.0")
        {
            Spec = new EvalSuiteSpec
            {
                AgentId = agentRef,
                Sampling = new EvalSamplingSpec { Rate = rate, WindowDuration = TimeSpan.FromHours(1) },
            },
        };

    private sealed class StubRegistry(List<EvalSuiteManifest> suites) : IEvalSuiteRegistry
    {
        public List<EvalSuiteManifest> Suites = suites;

        public async IAsyncEnumerable<EvalSuiteManifest> ListAsync(string? labelPrefix = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var s in Suites)
            {
                ct.ThrowIfCancellationRequested();
                yield return s;
            }
            await Task.CompletedTask;
        }

        public ValueTask<EvalSuiteManifest?> GetAsync(string id, string? version = null, CancellationToken ct = default) =>
            ValueTask.FromResult(Suites.Find(s => s.Id == id));

        public ValueTask UpsertAsync(EvalSuiteManifest manifest, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask RemoveAsync(string id, string version, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
