// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// CustomMemoryStore — implements IMemoryStore as a file-backed JSON dict,
// writes + reads + searches across three scopes, prints what's stored where.
// Session / Agent / Tenant scoping is demonstrated via MemoryScope equality.
// -----------------------------------------------------------------------------

var rootDir = Path.Combine(Path.GetTempPath(), "vais-memory-sample", Guid.NewGuid().ToString("N"));
Console.WriteLine($"Memory root: {rootDir}");
Console.WriteLine();

IMemoryStore store = new FileBackedMemoryStore(rootDir);

var sessionScope = new MemoryScope(SessionId: "conv-42",
    Durability: MemoryDurability.Working);
var agentScope = new MemoryScope(AgentId: "support",
    Durability: MemoryDurability.LongTerm);
var tenantScope = new MemoryScope(TenantId: "acme",
    Durability: MemoryDurability.LongTerm);

await store.WriteAsync(sessionScope, "last-intent", new MemoryItem("ask-for-refund"));
await store.WriteAsync(agentScope, "user.preferred-language", new MemoryItem("fr-FR"));
await store.WriteAsync(tenantScope, "brand.voice", new MemoryItem("warm, brief"));

Console.WriteLine("=== reads ===");
Console.WriteLine($"session last-intent   → {(await store.ReadAsync(sessionScope, "last-intent"))?.Content}");
Console.WriteLine($"agent  language       → {(await store.ReadAsync(agentScope, "user.preferred-language"))?.Content}");
Console.WriteLine($"tenant voice          → {(await store.ReadAsync(tenantScope, "brand.voice"))?.Content}");
Console.WriteLine();

// Cross-scope isolation: a session-scoped read can't see an agent-scoped write.
var miss = await store.ReadAsync(sessionScope, "user.preferred-language");
Console.WriteLine($"session read of agent-scoped key → {(miss is null ? "(null, scope-isolated)" : miss.Content)}");
Console.WriteLine();

Console.WriteLine("=== search (substring) in session scope ===");
await foreach (var r in store.SearchAsync(sessionScope, query: "refund"))
    Console.WriteLine($"  {r.Key} → {r.Item.Content}");

// ---- File-backed implementation ----
sealed class FileBackedMemoryStore(string root) : IMemoryStore
{
    private readonly ConcurrentDictionary<(MemoryScope, string), MemoryItem> _cache = new();

    private string PathFor(MemoryScope scope, string key)
    {
        var scopeDir = $"{scope.TenantId ?? "_"}_{scope.AgentId ?? "_"}_{scope.SessionId ?? "_"}_{scope.Durability}";
        var dir = Path.Combine(root, scopeDir);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Uri.EscapeDataString(key)}.txt");
    }

    public ValueTask WriteAsync(MemoryScope scope, string key, MemoryItem item, CancellationToken ct = default)
    {
        _cache[(scope, key)] = item;
        File.WriteAllText(PathFor(scope, key), item.Content);
        return ValueTask.CompletedTask;
    }

    public ValueTask<MemoryItem?> ReadAsync(MemoryScope scope, string key, CancellationToken ct = default)
    {
        if (_cache.TryGetValue((scope, key), out var cached)) return ValueTask.FromResult<MemoryItem?>(cached);
        var path = PathFor(scope, key);
        return ValueTask.FromResult<MemoryItem?>(
            File.Exists(path) ? new MemoryItem(File.ReadAllText(path)) : null);
    }

    public ValueTask<bool> DeleteAsync(MemoryScope scope, string key, CancellationToken ct = default)
    {
        _cache.TryRemove((scope, key), out _);
        var path = PathFor(scope, key);
        if (!File.Exists(path)) return ValueTask.FromResult(false);
        File.Delete(path);
        return ValueTask.FromResult(true);
    }

#pragma warning disable CS1998 // body is synchronous on purpose
    public async IAsyncEnumerable<MemorySearchResult> SearchAsync(
        MemoryScope scope,
        string query,
        int topK = 5,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var results = _cache
            .Where(kv => kv.Key.Item1 == scope &&
                         kv.Value.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new MemorySearchResult(kv.Key.Item2, kv.Value, Score: null))
            .Take(topK);
        foreach (var r in results) yield return r;
    }
#pragma warning restore CS1998
}
