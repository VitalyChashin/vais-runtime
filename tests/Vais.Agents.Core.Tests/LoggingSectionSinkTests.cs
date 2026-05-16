// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class LoggingSectionSinkTests
{
    private static SectionMeasurement M(
        string id,
        SectionKind kind = SectionKind.SystemSegment,
        string? producer = null,
        int chars = 0,
        int? tokens = null,
        double ratio = 0,
        string outcome = "included",
        int droppedChars = 0)
        => new(id, kind, producer, Order: null, Priority: null, chars, tokens, ratio, outcome, droppedChars);

    private static SectionTelemetrySnapshot Snap(AgentContext context, SectionBudgetSummary budget, params SectionMeasurement[] sections)
        => new(context, TurnIndex: 1, sections, budget);

    private static SectionBudgetSummary Budget(double usedRatio = 0, int dropped = 0, int truncated = 0)
        => new(null, null, 0, null, usedRatio, dropped, truncated);

    [Fact]
    public async Task Emits_One_Information_Entry_Per_Turn_With_Top_Level_Fields()
    {
        var logger = new CapturingLogger<LoggingSectionSink>();
        var sink = new LoggingSectionSink(logger);

        var ctx = new AgentContext { RunId = "run-1", AgentName = "researcher" };
        await sink.EmitAsync(Snap(ctx,
            new SectionBudgetSummary(TargetChars: 4096, TargetTokens: null, UsedChars: 1270, UsedTokens: null, UsedRatio: 0.31, DroppedCount: 0, TruncatedCount: 1),
            M("system.persona", chars: 32, ratio: 0.07),
            M("retrieval.docs", chars: 412, ratio: 0.89, outcome: "truncated", droppedChars: 180)));

        logger.Entries.Should().ContainSingle();
        var entry = logger.Entries[0];
        entry.Level.Should().Be(LogLevel.Information);
        entry.Properties["AgentId"].Should().Be("researcher");
        entry.Properties["RunId"].Should().Be("run-1");
        entry.Properties["TurnIndex"].Should().Be(1);
        entry.Properties["SectionCount"].Should().Be(2);
        entry.Properties["BudgetUsed"].Should().Be(0.31);
        entry.Properties["DroppedCount"].Should().Be(0);
        entry.Properties["TruncatedCount"].Should().Be(1);
    }

    [Fact]
    public async Task Sections_Json_Field_Carries_Per_Section_Detail()
    {
        var logger = new CapturingLogger<LoggingSectionSink>();
        var sink = new LoggingSectionSink(logger);

        await sink.EmitAsync(Snap(
            new AgentContext { AgentName = "a" },
            Budget(),
            M("system.persona", producer: "PersonaContributor", chars: 32, tokens: 8, ratio: 0.07),
            M("retrieval.docs", producer: "RAG", chars: 412, ratio: 0.89, outcome: "truncated", droppedChars: 180)));

        var json = logger.Entries[0].Properties["SectionsJson"]!.ToString()!;
        using var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement;
        arr.GetArrayLength().Should().Be(2);

        var s0 = arr[0];
        s0.GetProperty("id").GetString().Should().Be("system.persona");
        s0.GetProperty("kind").GetString().Should().Be("SystemSegment");
        s0.GetProperty("producer").GetString().Should().Be("PersonaContributor");
        s0.GetProperty("chars").GetInt32().Should().Be(32);
        s0.GetProperty("tokens").GetInt32().Should().Be(8);
        s0.GetProperty("ratio").GetDouble().Should().BeApproximately(0.07, 0.0001);
        s0.GetProperty("outcome").GetString().Should().Be("included");
        s0.TryGetProperty("dropped_chars", out _).Should().BeFalse(); // omitted when 0

        var s1 = arr[1];
        s1.GetProperty("outcome").GetString().Should().Be("truncated");
        s1.GetProperty("dropped_chars").GetInt32().Should().Be(180);
        s1.TryGetProperty("tokens", out _).Should().BeFalse(); // omitted when null
    }

    [Fact]
    public async Task Null_AgentName_And_RunId_Fall_Back_To_Unknown_Strings()
    {
        var logger = new CapturingLogger<LoggingSectionSink>();
        var sink = new LoggingSectionSink(logger);

        await sink.EmitAsync(Snap(AgentContext.Empty, Budget(), M("a", chars: 5, ratio: 1.0)));

        var entry = logger.Entries[0];
        entry.Properties["AgentId"].Should().Be("_unknown");
        entry.Properties["RunId"].Should().Be("_unknown");
    }

    [Fact]
    public async Task Disabled_Information_Level_Skips_Json_Serialization()
    {
        var logger = new CapturingLogger<LoggingSectionSink>(enabledLevels: new[] { LogLevel.Warning, LogLevel.Error });
        var sink = new LoggingSectionSink(logger);

        await sink.EmitAsync(Snap(AgentContext.Empty, Budget(), M("a", chars: 5, ratio: 1.0)));

        logger.Entries.Should().BeEmpty();
        logger.IsEnabledCallCount.Should().BeGreaterThan(0); // sink checked IsEnabled before doing work
    }

    [Fact]
    public async Task Null_Logger_In_Ctor_Throws()
    {
        Action act = () => new LoggingSectionSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Sections_Json_Field_Is_Empty_Array_When_Snapshot_Has_No_Sections()
    {
        var logger = new CapturingLogger<LoggingSectionSink>();
        var sink = new LoggingSectionSink(logger);

        await sink.EmitAsync(Snap(AgentContext.Empty, Budget()));

        var json = logger.Entries[0].Properties["SectionsJson"]!.ToString()!;
        json.Should().Be("[]");
        logger.Entries[0].Properties["SectionCount"].Should().Be(0);
    }

    // ─────────────────── Fixture ───────────────────

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly HashSet<LogLevel>? _enabled;
        public List<LogEntry> Entries { get; } = new();
        public int IsEnabledCallCount { get; private set; }

        public CapturingLogger(IEnumerable<LogLevel>? enabledLevels = null)
        {
            _enabled = enabledLevels is null ? null : new HashSet<LogLevel>(enabledLevels);
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
        {
            IsEnabledCallCount++;
            return _enabled is null || _enabled.Contains(logLevel);
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
            {
                foreach (var kv in list)
                {
                    // Standard MS.Extensions.Logging convention: the last entry under key "{OriginalFormat}"
                    // is the message template — keep it out of named props.
                    if (kv.Key == "{OriginalFormat}") continue;
                    props[kv.Key] = kv.Value;
                }
            }
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), props));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
