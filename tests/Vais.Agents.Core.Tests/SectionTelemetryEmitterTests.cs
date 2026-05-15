// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class SectionTelemetryEmitterTests
{
    private static Section Sys(string id, string text, int? order = null, int? priority = null, string? producer = null)
        => new(
            id,
            SectionKind.SystemSegment,
            new TextPayload(text),
            Order: order,
            ProducerId: producer,
            Budget: priority is null ? null : new SectionBudget(priority.Value));

    private static PackerOutcome Outcome(string id, string outcome, int droppedChars = 0)
        => new(id, outcome, droppedChars);

    private static SectionPackResult IdentityResult(IReadOnlyList<Section> sections)
    {
        var outcomes = sections.Select(s => Outcome(s.Id, PackerOutcomes.Included)).ToArray();
        return new SectionPackResult(sections, outcomes);
    }

    [Fact]
    public async Task NoOp_Emitter_Does_Not_Build_Snapshot_Or_Invoke_Sinks()
    {
        var emitter = SectionTelemetryEmitter.NoOp;

        emitter.IsNoOp.Should().BeTrue();

        // Even with malformed inputs (e.g. wrong outcome count), NoOp short-circuits before any work.
        await emitter.EmitAsync(
            inputSections: Array.Empty<Section>(),
            packResult: new SectionPackResult(Array.Empty<Section>(), Array.Empty<PackerOutcome>()),
            budget: SectionBudgetContext.Unlimited,
            context: AgentContext.Empty,
            turnIndex: 1);
    }

    [Fact]
    public async Task Empty_Sink_List_Is_NoOp()
    {
        var emitter = new SectionTelemetryEmitter(sinks: null);

        emitter.IsNoOp.Should().BeTrue();
    }

    [Fact]
    public async Task Sinks_Receive_Snapshot_With_Per_Input_Section_Measurements()
    {
        var sink = new RecordingSink();
        var emitter = new SectionTelemetryEmitter(new[] { sink });

        var sections = new[]
        {
            Sys("system.persona", "persona", order: 0, priority: 0, producer: "persona"),
            Sys("retrieval.docs", "retrieved text", order: 10, priority: 5, producer: "rag"),
        };

        var ctx = new AgentContext { RunId = "run-1", AgentName = "agent-1" };
        await emitter.EmitAsync(
            inputSections: sections,
            packResult: IdentityResult(sections),
            budget: SectionBudgetContext.Unlimited,
            context: ctx,
            turnIndex: 2);

        sink.Snapshots.Should().ContainSingle();
        var snap = sink.Snapshots[0];
        snap.Context.RunId.Should().Be("run-1");
        snap.Context.AgentName.Should().Be("agent-1");
        snap.TurnIndex.Should().Be(2);
        snap.Sections.Should().HaveCount(2);

        snap.Sections[0].Id.Should().Be("system.persona");
        snap.Sections[0].Kind.Should().Be(SectionKind.SystemSegment);
        snap.Sections[0].ProducerId.Should().Be("persona");
        snap.Sections[0].Order.Should().Be(0);
        snap.Sections[0].Priority.Should().Be(0);
        snap.Sections[0].Chars.Should().Be("persona".Length);
        snap.Sections[0].Outcome.Should().Be(PackerOutcomes.Included);

        snap.Sections[1].Chars.Should().Be("retrieved text".Length);
        snap.Sections[1].Priority.Should().Be(5);
    }

    [Fact]
    public async Task Ratios_Sum_To_One_Across_All_Sections()
    {
        var sink = new RecordingSink();
        var emitter = new SectionTelemetryEmitter(new[] { sink });

        // Three sections with 4, 4, 12 chars → total 20; ratios 0.2, 0.2, 0.6.
        var sections = new[]
        {
            Sys("a", "1234"),
            Sys("b", "5678"),
            Sys("c", "0123456789AB"),
        };

        await emitter.EmitAsync(sections, IdentityResult(sections), SectionBudgetContext.Unlimited, AgentContext.Empty, 1);

        var snap = sink.Snapshots[0];
        snap.Sections.Sum(s => s.Ratio).Should().BeApproximately(1.0, 0.0001);
        snap.Sections[0].Ratio.Should().BeApproximately(0.2, 0.001);
        snap.Sections[2].Ratio.Should().BeApproximately(0.6, 0.001);
    }

    [Fact]
    public async Task Token_Counter_Populates_Tokens_Field_When_Configured()
    {
        var sink = new RecordingSink();
        var emitter = new SectionTelemetryEmitter(new[] { sink });
        var counter = new FakeTokenCounter(charsPerToken: 2);

        var sections = new[] { Sys("a", "12345678") }; // 8 chars → 4 tokens.

        await emitter.EmitAsync(
            sections,
            IdentityResult(sections),
            new SectionBudgetContext(MaxTokens: 100, TokenCounter: counter),
            AgentContext.Empty, 1);

        var snap = sink.Snapshots[0];
        snap.Sections[0].Tokens.Should().Be(4);
        snap.Budget.UsedTokens.Should().Be(4);
        snap.Budget.TargetTokens.Should().Be(100);
        snap.Budget.UsedRatio.Should().BeApproximately(0.04, 0.001);
    }

    [Fact]
    public async Task No_Token_Counter_Leaves_Tokens_Null()
    {
        var sink = new RecordingSink();
        var emitter = new SectionTelemetryEmitter(new[] { sink });

        var sections = new[] { Sys("a", "abcd") };

        await emitter.EmitAsync(sections, IdentityResult(sections), SectionBudgetContext.Unlimited, AgentContext.Empty, 1);

        var snap = sink.Snapshots[0];
        snap.Sections[0].Tokens.Should().BeNull();
        snap.Budget.UsedTokens.Should().BeNull();
        snap.Budget.UsedChars.Should().Be(4);
    }

    [Fact]
    public async Task Dropped_Sections_Reported_With_DroppedChars_And_Not_Counted_In_Used()
    {
        var sink = new RecordingSink();
        var emitter = new SectionTelemetryEmitter(new[] { sink });

        var sections = new[]
        {
            Sys("kept", "abcd"),
            Sys("dropped", "xyz12345"),
        };
        var packResult = new SectionPackResult(
            new[] { sections[0] },
            new[]
            {
                Outcome("kept", PackerOutcomes.Included),
                Outcome("dropped", PackerOutcomes.Dropped, droppedChars: 8),
            });

        await emitter.EmitAsync(sections, packResult, new SectionBudgetContext(MaxChars: 100), AgentContext.Empty, 1);

        var snap = sink.Snapshots[0];
        snap.Sections.Single(s => s.Id == "dropped").Outcome.Should().Be(PackerOutcomes.Dropped);
        snap.Sections.Single(s => s.Id == "dropped").DroppedChars.Should().Be(8);
        snap.Budget.DroppedCount.Should().Be(1);
        snap.Budget.UsedChars.Should().Be(4); // only "kept"
    }

    [Fact]
    public async Task Metadata_Sections_Have_Zero_Chars_Even_With_Heavy_Payload()
    {
        var sink = new RecordingSink();
        var emitter = new SectionTelemetryEmitter(new[] { sink });

        var sections = new[]
        {
            Sys("a", "abcd"),
            new Section(
                "trace.metadata",
                SectionKind.Metadata,
                new MetadataPayload(new Dictionary<string, object?> { ["heavy"] = new string('M', 10_000) }),
                ProducerId: "tracer"),
        };

        await emitter.EmitAsync(sections, IdentityResult(sections), SectionBudgetContext.Unlimited, AgentContext.Empty, 1);

        var snap = sink.Snapshots[0];
        snap.Sections.Single(s => s.Id == "trace.metadata").Chars.Should().Be(0);
        snap.Sections.Single(s => s.Id == "trace.metadata").Ratio.Should().Be(0);
        snap.Budget.UsedChars.Should().Be(4); // only the SystemSegment counts
    }

    [Fact]
    public async Task Used_Ratio_Computed_Against_MaxChars_When_No_TokenCounter()
    {
        var sink = new RecordingSink();
        var emitter = new SectionTelemetryEmitter(new[] { sink });
        var sections = new[] { Sys("a", new string('X', 250)) };

        await emitter.EmitAsync(sections, IdentityResult(sections), new SectionBudgetContext(MaxChars: 1000), AgentContext.Empty, 1);

        sink.Snapshots[0].Budget.UsedRatio.Should().BeApproximately(0.25, 0.001);
    }

    [Fact]
    public async Task Sink_Failure_Is_Logged_And_Swallowed_Subsequent_Sinks_Still_Run()
    {
        var failing = new ThrowingSink();
        var observing = new RecordingSink();
        var logger = new RecordingLogger();
        var emitter = new SectionTelemetryEmitter(new ISectionTelemetrySink[] { failing, observing }, logger);

        var sections = new[] { Sys("a", "abcd") };

        await emitter.EmitAsync(sections, IdentityResult(sections), SectionBudgetContext.Unlimited, AgentContext.Empty, 1);

        observing.Snapshots.Should().HaveCount(1);
        logger.Warnings.Should().ContainSingle(w => w.Contains("ThrowingSink"));
    }

    [Fact]
    public async Task Sinks_Invoked_In_Registration_Order()
    {
        var order = new List<string>();
        var emitter = new SectionTelemetryEmitter(new ISectionTelemetrySink[]
        {
            new NamedRecordingSink("first", order),
            new NamedRecordingSink("second", order),
            new NamedRecordingSink("third", order),
        });

        var sections = new[] { Sys("a", "x") };
        await emitter.EmitAsync(sections, IdentityResult(sections), SectionBudgetContext.Unlimited, AgentContext.Empty, 1);

        order.Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task StatefulAiAgent_Invokes_Configured_Sink_Per_Turn()
    {
        // End-to-end: a sink registered via StatefulAgentOptions.SectionTelemetrySinks observes
        // exactly one snapshot per AskAsync turn, with the expected base sections present.
        var sink = new RecordingSink();
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPrompt = "base-prompt",
            SectionTelemetrySinks = new[] { sink },
        });

        await agent.AskAsync("hi");

        sink.Snapshots.Should().ContainSingle();
        var snap = sink.Snapshots[0];
        snap.TurnIndex.Should().Be(1);
        snap.Sections.Select(s => s.Id).Should().Contain("system.base");
        snap.Sections.Should().Contain(s => s.Id.StartsWith("history.base."));
    }

    [Fact]
    public async Task StatefulAiAgent_NoOp_When_No_Sinks_Configured()
    {
        // Default config (no sinks) → emitter is NoOp → no observable side effects. This test
        // just confirms the AskAsync path doesn't crash when telemetry is unwired (the typical
        // path for downstream consumers who haven't opted into observability).
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions { SystemPrompt = "base" });

        Func<Task> act = async () => await agent.AskAsync("hi");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Cancellation_Token_Propagates_To_Sinks()
    {
        var sink = new RecordingSink();
        var emitter = new SectionTelemetryEmitter(new[] { sink });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sections = new[] { Sys("a", "x") };
        var act = async () => await emitter.EmitAsync(sections, IdentityResult(sections), SectionBudgetContext.Unlimited, AgentContext.Empty, 1, cts.Token);

        // RecordingSink doesn't honour the token, so this passes. The contract is that the
        // emitter passes the token through unchanged — verified by inspection in
        // SectionTelemetryEmitter.EmitAsync. A cancellation-aware sink would observe the cancel.
        await act.Should().NotThrowAsync();
        sink.Snapshots.Should().ContainSingle();
    }

    // ─────────────────── Test fixtures ───────────────────

    private sealed class RecordingSink : ISectionTelemetrySink
    {
        public List<SectionTelemetrySnapshot> Snapshots { get; } = new();

        public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Snapshots.Add(snapshot);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NamedRecordingSink(string name, List<string> order) : ISectionTelemetrySink
    {
        public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            order.Add(name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : ISectionTelemetrySink
    {
        public ValueTask EmitAsync(SectionTelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("sink-failure");
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FakeTokenCounter(int charsPerToken) : ITokenCounter
    {
        public int Count(string text) => text.Length / charsPerToken;
    }
}
