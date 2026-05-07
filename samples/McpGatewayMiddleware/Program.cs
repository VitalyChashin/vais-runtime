// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// McpGatewayMiddleware — compose MCP tool gateway middleware in C#:
//   retry, deterministic cache, argument validation.
//
// Run: dotnet run --project samples/McpGatewayMiddleware
// Prereq: gateway packages built to artifacts/packages/ (see README)
// Env: none (scripted tools, no API key)
// Docs: docs/concepts/mcp-gateway.md
//
// Three passes demonstrating the tool gateway middleware pipeline directly
// (without a full agent loop) by composing ToolGatewayMiddleware instances
// and invoking them with a scripted ToolGatewayContext:
//  1. ToolRetryMiddleware         — tool fails twice; retried to success on 3rd attempt
//  2. ToolResultCacheMiddleware   — same call twice; 2nd served from cache, tool invoked once
//  3. ToolArgumentValidationMiddleware — missing required arg returns ToolDenied, tool not called

using System.Text.Json;
using Vais.Agents;
using Vais.Agents.Gateways.McpCache;
using Vais.Agents.Gateways.McpReliability;
using Vais.Agents.Gateways.McpSecurity;

var ct = CancellationToken.None;

// ---- 1: ToolRetryMiddleware ----
Console.WriteLine("== 1 — ToolRetryMiddleware (fails twice → succeeds on 3rd attempt) ==");
var flaky = new FlakyTool(failTimes: 2);
var retry = new ToolRetryMiddleware(maxAttempts: 3, initialDelay: TimeSpan.FromMilliseconds(10));
var ctx1  = MakeCtx("get_weather", @"{""location"":""Tokyo""}");
var r1    = await Run(ctx1, ct, [retry], () => flaky.Execute(ctx1));
Console.WriteLine($"  result:   \"{r1.Result}\"");
Console.WriteLine($"  error:    {r1.Error ?? "(none)"}");
Console.WriteLine($"  attempts: {flaky.Attempts}  (expected 3)");
Console.WriteLine();

// ---- 2: ToolResultCacheMiddleware ----
Console.WriteLine("== 2 — ToolResultCacheMiddleware (same args → cache hit on 2nd call) ==");
var cache  = new InMemoryToolResultCache();
var cacheM = new ToolResultCacheMiddleware(cache);
var ctool  = new CountingTool();
var ctx2a  = MakeCtx("get_weather", @"{""location"":""Tokyo""}");
var ctx2b  = MakeCtx("get_weather", @"{""location"":""Tokyo""}");
var r2a    = await Run(ctx2a, ct, [cacheM], () => ctool.Execute(ctx2a));
var r2b    = await Run(ctx2b, ct, [cacheM], () => ctool.Execute(ctx2b));
Console.WriteLine($"  call 1: \"{r2a.Result}\"");
Console.WriteLine($"  call 2: \"{r2b.Result}\"  (from cache)");
Console.WriteLine($"  tool invoked: {ctool.Invocations} time(s)  (expected 1)");
Console.WriteLine($"  bodies match: {r2a.Result == r2b.Result}");
Console.WriteLine();

// ---- 3: ToolArgumentValidationMiddleware ----
Console.WriteLine("== 3 — ToolArgumentValidationMiddleware (missing arg → ToolDenied) ==");
var requiredArgs = new Dictionary<string, IReadOnlyList<string>>
{
    ["get_weather"] = ["location"],
};
var validation = new ToolArgumentValidationMiddleware(requiredArgs);
var noop = new NoOpTool();

var ctx3a = MakeCtx("get_weather", @"{""location"":""Tokyo""}");
var r3a   = await Run(ctx3a, ct, [validation], () => noop.Execute(ctx3a));
Console.WriteLine($"  with 'location': error={r3a.Error ?? "(none)"}  tool called={noop.Called}");

noop.Called = false;
var ctx3b = MakeCtx("get_weather", @"{}");  // missing required 'location'
var r3b   = await Run(ctx3b, ct, [validation], () => noop.Execute(ctx3b));
Console.WriteLine($"  missing arg:     error={r3b.Error}  tool called={noop.Called}");

Console.WriteLine();
Console.WriteLine("Done.");

// ---- helpers ----
static ToolGatewayContext MakeCtx(string toolName, string argsJson) =>
    new(toolName, Guid.NewGuid().ToString("N")[..8], JsonDocument.Parse(argsJson).RootElement, AgentContext.Empty);

// Compose middleware pipeline: outer-to-inner, final delegate at the end.
static Task<ToolCallOutcome> Run(
    ToolGatewayContext ctx,
    CancellationToken ct,
    IReadOnlyList<ToolGatewayMiddleware> middleware,
    Func<Task<ToolCallOutcome>> final)
{
    var next = final;
    for (var i = middleware.Count - 1; i >= 0; i--)
    {
        var mw      = middleware[i];
        var capture = next;
        next = () => mw.InvokeAsync(ctx, capture, ct);
    }
    return next();
}

// ---- scripted tools ----
sealed class FlakyTool(int failTimes)
{
    public int Attempts;
    public Task<ToolCallOutcome> Execute(ToolGatewayContext ctx)
    {
        Attempts++;
        return Attempts <= failTimes
            ? Task.FromResult(new ToolCallOutcome(ctx.CallId, Result: string.Empty, Error: "transient error"))
            : Task.FromResult(new ToolCallOutcome(ctx.CallId, Result: @"{""temp"":18,""condition"":""Sunny""}"));
    }
}

sealed class CountingTool
{
    public int Invocations;
    public Task<ToolCallOutcome> Execute(ToolGatewayContext ctx)
    {
        Invocations++;
        return Task.FromResult(new ToolCallOutcome(ctx.CallId, Result: @"{""temp"":18}"));
    }
}

sealed class NoOpTool
{
    public bool Called;
    public Task<ToolCallOutcome> Execute(ToolGatewayContext ctx)
    {
        Called = true;
        return Task.FromResult(new ToolCallOutcome(ctx.CallId, Result: @"{""temp"":18}"));
    }
}
