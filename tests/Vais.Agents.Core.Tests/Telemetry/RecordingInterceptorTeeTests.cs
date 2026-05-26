// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests.Telemetry;

/// <summary>
/// D-3 verify gate: RecordingInterceptorTee adapts InterceptorTeeEvent into TrajectoryEvent
/// and appends to the store. Fields are projected correctly; PII-shaped args are redacted at
/// tee time; append failures are swallowed (fire-and-forget contract).
/// </summary>
public sealed class RecordingInterceptorTeeTests
{
    [Fact]
    public async Task EmitAsync_ProjectsToolCallPayload_IntoTrajectoryEvent()
    {
        var store = new InMemoryInterceptorTeeStore();
        var tee = new RecordingInterceptorTee(store);
        var args = JsonDocument.Parse("""{"url":"https://example.com","apiKey":"sk-xyz","count":3}""").RootElement;

        await tee.EmitAsync(new InterceptorTeeEvent
        {
            EventName = "tool.call",
            Context = new TestContext
            {
                Operation = OntologyOperation.Call,
                AgentContext = new AgentContext(AgentName: "coord") { RunId = "run-1" },
            },
            Payload = new ToolCallTrajectoryPayload(
                ConceptName: "fetch_url",
                Transport: "south",
                Arguments: args,
                Outcome: new TrajectoryOutcome(TrajectoryOutcomeKind.Ok),
                Duration: TimeSpan.FromMilliseconds(120)),
        });

        store.Count.Should().Be(1);
        var stored = await Single(store);
        stored.EventName.Should().Be("tool.call");
        stored.Operation.Should().Be(OntologyOperation.Call);
        stored.AgentId.Should().Be("coord");
        stored.RunId.Should().Be("run-1");
        stored.ConceptName.Should().Be("fetch_url");
        stored.Transport.Should().Be("south");
        stored.Outcome.Should().Be(new TrajectoryOutcome(TrajectoryOutcomeKind.Ok));
        stored.Duration.Should().Be(TimeSpan.FromMilliseconds(120));
        stored.ArgumentsShape.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["url"] = "string",
            ["count"] = "number",
        }, "apiKey is omitted by the default redactor");
    }

    [Fact]
    public async Task EmitAsync_WithoutTypedPayload_StillProducesAValidTrajectoryEvent()
    {
        var store = new InMemoryInterceptorTeeStore();
        var tee = new RecordingInterceptorTee(store);

        await tee.EmitAsync(new InterceptorTeeEvent
        {
            EventName = "north.list",
            Context = new TestContext
            {
                Operation = OntologyOperation.List,
                AgentContext = AgentContext.Empty,
            },
            Payload = null,
        });

        var stored = await Single(store);
        stored.EventName.Should().Be("north.list");
        stored.Operation.Should().Be(OntologyOperation.List);
        stored.ConceptName.Should().BeNull();
        stored.Transport.Should().BeNull();
        stored.ArgumentsShape.Should().BeNull();
        stored.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task EmitAsync_CarriesOntologyVersionFromBinding()
    {
        var store = new InMemoryInterceptorTeeStore();
        var tee = new RecordingInterceptorTee(store);

        await tee.EmitAsync(new InterceptorTeeEvent
        {
            EventName = "tool.call",
            Context = new TestContext
            {
                Operation = OntologyOperation.Call,
                AgentContext = AgentContext.Empty,
                Binding = new FakeBinding("ont-v7"),
            },
        });

        var stored = await Single(store);
        stored.OntologyVersion.Should().Be("ont-v7");
    }

    [Fact]
    public async Task EmitAsync_GeneratesUniqueEventIdsAndUtcTimestamps()
    {
        var store = new InMemoryInterceptorTeeStore();
        var tee = new RecordingInterceptorTee(store);

        for (var i = 0; i < 5; i++)
            await tee.EmitAsync(new InterceptorTeeEvent
            {
                EventName = "x",
                Context = new TestContext { Operation = OntologyOperation.Call, AgentContext = AgentContext.Empty },
            });

        var all = await Collect(store);
        all.Select(e => e.EventId).Distinct().Should().HaveCount(5);
        all.Should().AllSatisfy(e => e.Timestamp.Offset.Should().Be(TimeSpan.Zero));
    }

    [Fact]
    public async Task EmitAsync_SwallowsStoreFailures_FireAndForgetContract()
    {
        var tee = new RecordingInterceptorTee(new ThrowingStore());

        await FluentActions.Awaiting(() => tee.EmitAsync(new InterceptorTeeEvent
        {
            EventName = "x",
            Context = new TestContext { Operation = OntologyOperation.Call, AgentContext = AgentContext.Empty },
        }).AsTask()).Should().NotThrowAsync("store failures must never break the interception lifecycle");
    }

    [Fact]
    public void Constructor_RejectsNullStore()
    {
        FluentActions.Invoking(() => new RecordingInterceptorTee(null!))
            .Should().Throw<ArgumentNullException>();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed class TestContext : InterceptionContext;

    private sealed class FakeBinding(string version) : IOntologyBinding
    {
        public string OntologyVersion { get; } = version;
        public IReadOnlyList<string> ConceptNames => [];
        public bool TryGetConcept(string conceptName, out OntologyConceptEntry entry)
        {
            entry = null!;
            return false;
        }
    }

    private sealed class ThrowingStore : IInterceptorTeeStore
    {
        public ValueTask AppendAsync(TrajectoryEvent trajectoryEvent, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store is down");

        public async IAsyncEnumerable<TrajectoryEvent> QueryAsync(
            TrajectoryQuery query,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static async Task<TrajectoryEvent> Single(IInterceptorTeeStore store)
    {
        await foreach (var e in store.QueryAsync(new TrajectoryQuery())) return e;
        throw new InvalidOperationException("store empty");
    }

    private static async Task<List<TrajectoryEvent>> Collect(IInterceptorTeeStore store)
    {
        var list = new List<TrajectoryEvent>();
        await foreach (var e in store.QueryAsync(new TrajectoryQuery())) list.Add(e);
        return list;
    }
}
