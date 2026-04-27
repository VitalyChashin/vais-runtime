// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Gateways.McpCache;
using Xunit;

namespace Vais.Agents.Gateways.McpCache.Tests;

public sealed class McpCacheTests
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private static ToolGatewayContext MakeContext(string toolName = "tool", string callId = "c1")
        => new(toolName, callId, EmptyArgs, AgentContext.Empty);

    // ── InMemoryToolResultCache ──────────────────────────────────────────────

    [Fact]
    public async Task InMemoryCache_Miss_Returns_Null()
    {
        var cache = new InMemoryToolResultCache();
        var result = await cache.TryGetAsync("tool", EmptyArgs);
        result.Should().BeNull();
    }

    [Fact]
    public async Task InMemoryCache_Set_Then_Get_Returns_Stored_Outcome()
    {
        var cache = new InMemoryToolResultCache();
        var stored = new ToolCallOutcome("c1", "cached");
        await cache.SetAsync("tool", EmptyArgs, stored);

        var hit = await cache.TryGetAsync("tool", EmptyArgs);

        hit.Should().NotBeNull();
        hit!.Result.Should().Be("cached");
    }

    // ── ToolResultCacheMiddleware ────────────────────────────────────────────

    [Fact]
    public async Task Cache_Miss_Calls_Tool_And_Stores_Outcome()
    {
        var cache = new InMemoryToolResultCache();
        var mw = new ToolResultCacheMiddleware(cache);
        var ctx = MakeContext("query");
        var invoked = false;
        Func<Task<ToolCallOutcome>> next = () =>
        {
            invoked = true;
            return Task.FromResult(new ToolCallOutcome("c1", "live"));
        };

        var outcome = await mw.InvokeAsync(ctx, next, CancellationToken.None);

        invoked.Should().BeTrue();
        outcome.Result.Should().Be("live");

        // Second call should hit cache
        invoked = false;
        var ctx2 = MakeContext("query", callId: "c2");
        var outcome2 = await mw.InvokeAsync(ctx2, next, CancellationToken.None);
        invoked.Should().BeFalse();
        outcome2.Result.Should().Be("live");
    }

    [Fact]
    public async Task Cache_Hit_Remaps_CallId_To_Current_Context()
    {
        var cache = new InMemoryToolResultCache();
        var mw = new ToolResultCacheMiddleware(cache);
        Func<Task<ToolCallOutcome>> next = () =>
            Task.FromResult(new ToolCallOutcome("original-call", "result"));

        // Prime the cache
        await mw.InvokeAsync(MakeContext(callId: "original-call"), next, CancellationToken.None);

        // Retrieve with different callId
        var outcome = await mw.InvokeAsync(MakeContext(callId: "new-call"), next, CancellationToken.None);

        outcome.CallId.Should().Be("new-call");
        outcome.Result.Should().Be("result");
    }

    [Fact]
    public async Task Cache_Does_Not_Store_Error_Outcomes()
    {
        var cache = new InMemoryToolResultCache();
        var mw = new ToolResultCacheMiddleware(cache);
        var ctx = MakeContext("flaky");
        var attempt = 0;
        Func<Task<ToolCallOutcome>> next = () =>
        {
            attempt++;
            return Task.FromResult(attempt == 1
                ? new ToolCallOutcome("c1", "err", "SomeError")
                : new ToolCallOutcome("c1", "ok"));
        };

        await mw.InvokeAsync(ctx, next, CancellationToken.None);
        var outcome = await mw.InvokeAsync(MakeContext("flaky", "c2"), next, CancellationToken.None);

        // Tool must have been called a second time (not cache hit from the error)
        attempt.Should().Be(2);
        outcome.Error.Should().BeNull();
    }

    [Fact]
    public async Task Cache_Excluded_Tool_Bypasses_Cache()
    {
        var cache = new InMemoryToolResultCache();
        var mw = new ToolResultCacheMiddleware(cache, excludedTools: ["noncacheable"]);
        var ctx = MakeContext("noncacheable");
        var calls = 0;
        Func<Task<ToolCallOutcome>> next = () =>
        {
            calls++;
            return Task.FromResult(new ToolCallOutcome("c1", "live"));
        };

        await mw.InvokeAsync(ctx, next, CancellationToken.None);
        await mw.InvokeAsync(ctx, next, CancellationToken.None);

        calls.Should().Be(2);
    }
}
