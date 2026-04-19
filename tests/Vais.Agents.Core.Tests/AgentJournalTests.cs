// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class NullAgentJournalTests
{
    [Fact]
    public async Task Appends_Are_No_Op_And_Reads_Yield_Nothing()
    {
        var journal = NullAgentJournal.Instance;
        var entry = MakeEntry("run-1", "call-1");

        await journal.AppendAsync(entry);
        var read = await CollectAsync(journal.ReadAsync("run-1"));
        await journal.ClearAsync("run-1");

        read.Should().BeEmpty();
    }

    private static JournalEntry MakeEntry(string runId, string callId)
        => new ToolCallRecorded(
            RunId: runId,
            CallId: callId,
            ToolName: "echo",
            Arguments: JsonDocument.Parse("{}").RootElement,
            Outcome: new ToolCallOutcome(callId, "ok"),
            At: DateTimeOffset.UtcNow);

    private static async Task<List<JournalEntry>> CollectAsync(IAsyncEnumerable<JournalEntry> source)
    {
        var list = new List<JournalEntry>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}

public sealed class InMemoryAgentJournalTests
{
    [Fact]
    public async Task Single_Append_Round_Trips()
    {
        var journal = new InMemoryAgentJournal();
        var entry = MakeEntry("run-1", "call-1", "first");

        await journal.AppendAsync(entry);
        var read = await CollectAsync(journal.ReadAsync("run-1"));

        read.Should().HaveCount(1);
        read[0].Should().BeOfType<ToolCallRecorded>()
            .Which.CallId.Should().Be("call-1");
        ((ToolCallRecorded)read[0]).Outcome.Result.Should().Be("first");
    }

    [Fact]
    public async Task Reads_Preserve_Append_Order()
    {
        var journal = new InMemoryAgentJournal();
        for (var i = 0; i < 5; i++)
        {
            await journal.AppendAsync(MakeEntry("run-1", $"c{i}", $"r{i}"));
        }

        var read = await CollectAsync(journal.ReadAsync("run-1"));
        read.Select(e => ((ToolCallRecorded)e).CallId)
            .Should().Equal("c0", "c1", "c2", "c3", "c4");
    }

    [Fact]
    public async Task Clear_Removes_All_Entries_For_Run()
    {
        var journal = new InMemoryAgentJournal();
        await journal.AppendAsync(MakeEntry("run-1", "c1"));
        await journal.AppendAsync(MakeEntry("run-2", "c1"));

        await journal.ClearAsync("run-1");

        (await CollectAsync(journal.ReadAsync("run-1"))).Should().BeEmpty();
        (await CollectAsync(journal.ReadAsync("run-2"))).Should().HaveCount(1);
    }

    [Fact]
    public async Task Clear_Of_Unknown_Run_Is_No_Op()
    {
        var journal = new InMemoryAgentJournal();
        await journal.ClearAsync("never-appended"); // should not throw
    }

    [Fact]
    public async Task Concurrent_Appends_Across_Runs_Do_Not_Block_Each_Other()
    {
        var journal = new InMemoryAgentJournal();
        var tasks = new List<Task>();
        for (var r = 0; r < 4; r++)
        {
            var runId = $"run-{r}";
            tasks.Add(Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    await journal.AppendAsync(MakeEntry(runId, $"c{i}"));
                }
            }));
        }
        await Task.WhenAll(tasks);

        for (var r = 0; r < 4; r++)
        {
            (await CollectAsync(journal.ReadAsync($"run-{r}"))).Should().HaveCount(50);
        }
    }

    [Fact]
    public async Task Concurrent_Appends_On_Same_Run_Produce_All_Entries()
    {
        var journal = new InMemoryAgentJournal();
        const int writers = 8;
        const int perWriter = 25;

        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                await journal.AppendAsync(MakeEntry("run-x", $"w{w}-i{i}"));
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        var entries = await CollectAsync(journal.ReadAsync("run-x"));
        entries.Should().HaveCount(writers * perWriter);
    }

    [Fact]
    public async Task Append_Null_Entry_Throws()
    {
        var journal = new InMemoryAgentJournal();
        await FluentActions.Invoking(async () => await journal.AppendAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Read_Unknown_Run_Yields_Nothing()
    {
        var journal = new InMemoryAgentJournal();
        (await CollectAsync(journal.ReadAsync("absent"))).Should().BeEmpty();
    }

    private static JournalEntry MakeEntry(string runId, string callId, string result = "ok")
        => new ToolCallRecorded(
            RunId: runId,
            CallId: callId,
            ToolName: "echo",
            Arguments: JsonDocument.Parse("""{"x":1}""").RootElement,
            Outcome: new ToolCallOutcome(callId, result),
            At: DateTimeOffset.UtcNow);

    private static async Task<List<JournalEntry>> CollectAsync(IAsyncEnumerable<JournalEntry> source)
    {
        var list = new List<JournalEntry>();
        await foreach (var item in source) list.Add(item);
        return list;
    }
}

public sealed class StatefulAgentOptionsJournalTests
{
    [Fact]
    public void Journal_Defaults_To_Null()
    {
        var options = new StatefulAgentOptions();
        options.Journal.Should().BeNull();
    }

    [Fact]
    public void Journal_Can_Be_Set_At_Construction()
    {
        var journal = new InMemoryAgentJournal();
        var options = new StatefulAgentOptions { Journal = journal };
        options.Journal.Should().BeSameAs(journal);
    }
}
