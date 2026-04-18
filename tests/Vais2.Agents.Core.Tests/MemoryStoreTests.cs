// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais2.Agents.Core.Tests;

public sealed class InMemoryMemoryStoreTests
{
    [Fact]
    public async Task Write_Then_Read_Roundtrips()
    {
        var store = new InMemoryMemoryStore();
        var scope = new MemoryScope(SessionId: "s1");
        var item = new MemoryItem("hello", CreatedAt: DateTimeOffset.UtcNow);

        await store.WriteAsync(scope, "k1", item);
        var read = await store.ReadAsync(scope, "k1");

        read.Should().NotBeNull();
        read!.Content.Should().Be("hello");
    }

    [Fact]
    public async Task Read_Missing_Returns_Null()
    {
        var store = new InMemoryMemoryStore();
        var read = await store.ReadAsync(new MemoryScope(SessionId: "s1"), "absent");
        read.Should().BeNull();
    }

    [Fact]
    public async Task Scope_Isolates_Items()
    {
        var store = new InMemoryMemoryStore();
        var s1 = new MemoryScope(SessionId: "s1");
        var s2 = new MemoryScope(SessionId: "s2");

        await store.WriteAsync(s1, "k", new MemoryItem("from-s1"));
        await store.WriteAsync(s2, "k", new MemoryItem("from-s2"));

        (await store.ReadAsync(s1, "k"))!.Content.Should().Be("from-s1");
        (await store.ReadAsync(s2, "k"))!.Content.Should().Be("from-s2");
    }

    [Fact]
    public async Task Scope_With_Different_Durability_Is_A_Different_Partition()
    {
        var store = new InMemoryMemoryStore();
        var shortTerm = new MemoryScope(SessionId: "s", Durability: MemoryDurability.ShortTerm);
        var longTerm = new MemoryScope(SessionId: "s", Durability: MemoryDurability.LongTerm);

        await store.WriteAsync(shortTerm, "k", new MemoryItem("ephemeral"));

        (await store.ReadAsync(shortTerm, "k"))!.Content.Should().Be("ephemeral");
        (await store.ReadAsync(longTerm, "k")).Should().BeNull();
    }

    [Fact]
    public async Task Overwrite_Replaces_Item()
    {
        var store = new InMemoryMemoryStore();
        var scope = new MemoryScope(SessionId: "s");

        await store.WriteAsync(scope, "k", new MemoryItem("v1"));
        await store.WriteAsync(scope, "k", new MemoryItem("v2"));

        (await store.ReadAsync(scope, "k"))!.Content.Should().Be("v2");
    }

    [Fact]
    public async Task Delete_Removes_Item()
    {
        var store = new InMemoryMemoryStore();
        var scope = new MemoryScope(SessionId: "s");
        await store.WriteAsync(scope, "k", new MemoryItem("v"));

        (await store.DeleteAsync(scope, "k")).Should().BeTrue();
        (await store.ReadAsync(scope, "k")).Should().BeNull();
        (await store.DeleteAsync(scope, "k")).Should().BeFalse();
    }

    [Fact]
    public async Task Search_Matches_Case_Insensitive_Substring()
    {
        var store = new InMemoryMemoryStore();
        var scope = new MemoryScope(AgentId: "a");
        await store.WriteAsync(scope, "k1", new MemoryItem("The Quick Brown Fox"));
        await store.WriteAsync(scope, "k2", new MemoryItem("Lazy Dog"));
        await store.WriteAsync(scope, "k3", new MemoryItem("Another Fox"));

        var hits = await CollectAsync(store.SearchAsync(scope, "fox", topK: 10));

        hits.Should().HaveCount(2);
        hits.Select(h => h.Key).Should().BeEquivalentTo(new[] { "k1", "k3" });
    }

    [Fact]
    public async Task Search_Honours_TopK()
    {
        var store = new InMemoryMemoryStore();
        var scope = new MemoryScope(AgentId: "a");
        for (var i = 0; i < 5; i++)
        {
            await store.WriteAsync(scope, $"k{i}", new MemoryItem($"item-{i}"));
        }

        var hits = await CollectAsync(store.SearchAsync(scope, "item", topK: 2));

        hits.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_Empty_Query_Enumerates_Partition()
    {
        var store = new InMemoryMemoryStore();
        var scope = new MemoryScope(AgentId: "a");
        await store.WriteAsync(scope, "k1", new MemoryItem("one"));
        await store.WriteAsync(scope, "k2", new MemoryItem("two"));

        var hits = await CollectAsync(store.SearchAsync(scope, string.Empty, topK: 10));

        hits.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_In_Unknown_Scope_Yields_Nothing()
    {
        var store = new InMemoryMemoryStore();
        var hits = await CollectAsync(store.SearchAsync(new MemoryScope(SessionId: "absent"), "q"));
        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task NullMemoryStore_Is_No_Op()
    {
        var store = NullMemoryStore.Instance;
        var scope = new MemoryScope();

        await store.WriteAsync(scope, "k", new MemoryItem("v"));
        (await store.ReadAsync(scope, "k")).Should().BeNull();
        (await store.DeleteAsync(scope, "k")).Should().BeFalse();
        (await CollectAsync(store.SearchAsync(scope, "q"))).Should().BeEmpty();
    }

    private static async Task<List<MemorySearchResult>> CollectAsync(IAsyncEnumerable<MemorySearchResult> source)
    {
        var list = new List<MemorySearchResult>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }
}

public sealed class HistoryReducerTests
{
    [Fact]
    public async Task NoopHistoryReducer_Returns_Input_Unchanged()
    {
        var history = new[]
        {
            new ChatTurn(AgentChatRole.User, "one"),
            new ChatTurn(AgentChatRole.Assistant, "two"),
        };

        var reduced = await NoopHistoryReducer.Instance.ReduceAsync(history);

        reduced.Should().BeSameAs(history);
    }

    [Fact]
    public async Task StatefulAiAgent_Applies_Reducer_To_Snapshot_Before_Provider_Call()
    {
        var observed = new List<IReadOnlyList<ChatTurn>>();
        var provider = new FakeCompletionProvider(req =>
        {
            observed.Add(req.History);
            return new CompletionResponse("ok");
        });

        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            HistoryReducer = new LastNReducer(1),
        });

        await agent.AskAsync("first");
        await agent.AskAsync("second");

        // First call: session has 1 user turn, reducer caps at 1 → provider sees 1.
        // Second call: session has 3 turns (user/assistant/user), reducer caps at 1 → provider sees 1 (the latest user turn).
        observed.Should().HaveCount(2);
        observed[0].Should().HaveCount(1);
        observed[0][0].Text.Should().Be("first");
        observed[1].Should().HaveCount(1);
        observed[1][0].Text.Should().Be("second");

        // Session history itself is NOT reduced — the reducer's output is advisory / per-turn.
        agent.Session.History.Should().HaveCount(4);
    }

    private sealed class LastNReducer(int n) : IHistoryReducer
    {
        public ValueTask<IReadOnlyList<ChatTurn>> ReduceAsync(
            IReadOnlyList<ChatTurn> history,
            CancellationToken cancellationToken = default)
        {
            if (history.Count <= n) return ValueTask.FromResult(history);
            IReadOnlyList<ChatTurn> tail = history.Skip(history.Count - n).ToArray();
            return ValueTask.FromResult(tail);
        }
    }
}
