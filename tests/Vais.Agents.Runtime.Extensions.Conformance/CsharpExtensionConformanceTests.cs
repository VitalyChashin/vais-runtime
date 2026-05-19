// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Conformance;

/// <summary>
/// Runs the host-agnostic conformance suite and the csharp-specific invocation tests
/// against <c>host: csharp</c> (in-process ALC) extensions.
/// Handler instances are created directly in-process — no subprocess or HTTP round-trip.
/// </summary>
public sealed class CsharpExtensionConformanceTests : ExtensionConformanceBase
{
    // Use this assembly as the ALC path (real DLL on disk; no VaisExtension attribute so no handlers are discovered automatically).
    private static readonly string AssemblyPath =
        typeof(CsharpExtensionConformanceTests).Assembly.Location;

    protected override Task<ExtensionDescriptor> CreateDescriptorAsync(
        string extensionId, ExtensionScope? scope, int priority)
    {
        var manifest = MakeManifest(extensionId, "1.0.0", scope);
        var binding = new HandlerBinding(
            HandlerId: $"{extensionId}-in",
            Seam: ExtensionSeams.AgentInput,
            Priority: priority,
            FailureMode: "log",
            HandlerInstance: new NoOpInputMiddleware());

        return Task.FromResult(new ExtensionDescriptor(
            ExtensionId: extensionId,
            Version: "1.0.0",
            Manifest: manifest,
            Handlers: new[] { binding },
            LoadContext: new ExtensionAssemblyLoadContext(AssemblyPath)));
    }

    // ── csharp invocation tests ────────────────────────────────────────────

    // C-1. Passthrough: handler calls next()
    [Fact]
    public async Task CsharpHandler_Passthrough_CallsNext()
    {
        var (registry, composer) = MakeChain();
        var descriptor = await CreateDescriptorAsync("ext-pass", scope: null, priority: 100);
        await registry.SwapAsync("ext-pass", descriptor);

        var chain = await composer.GetInputChainAsync("agent-a");

        bool nextCalled = false;
        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "hello" };
        await chain[0].InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue("passthrough handler must call next()");
    }

    // C-2. Short-circuit: handler does NOT call next()
    [Fact]
    public async Task CsharpHandler_ShortCircuit_SuppressesNext()
    {
        var (registry, composer) = MakeChain();
        var binding = new HandlerBinding(
            HandlerId: "sc-in",
            Seam: ExtensionSeams.AgentInput,
            Priority: 100,
            FailureMode: "log",
            HandlerInstance: new ShortCircuitInputMiddleware());

        var descriptor = new ExtensionDescriptor(
            ExtensionId: "ext-sc",
            Version: "1.0.0",
            Manifest: MakeManifest("ext-sc", "1.0.0"),
            Handlers: new[] { binding },
            LoadContext: new ExtensionAssemblyLoadContext(AssemblyPath));

        await registry.SwapAsync("ext-sc", descriptor);
        var chain = await composer.GetInputChainAsync("agent-a");

        bool nextCalled = false;
        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "hello" };
        await chain[0].InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeFalse("short-circuit handler must NOT call next()");
    }

    // C-3. Context mutation is visible to the caller
    [Fact]
    public async Task CsharpHandler_ContextMutation_IsVisibleToCaller()
    {
        var (registry, composer) = MakeChain();
        var binding = new HandlerBinding(
            HandlerId: "mut-in",
            Seam: ExtensionSeams.AgentInput,
            Priority: 100,
            FailureMode: "log",
            HandlerInstance: new PropertySetterInputMiddleware("result", "mutated"));

        var descriptor = new ExtensionDescriptor(
            ExtensionId: "ext-mut",
            Version: "1.0.0",
            Manifest: MakeManifest("ext-mut", "1.0.0"),
            Handlers: new[] { binding },
            LoadContext: new ExtensionAssemblyLoadContext(AssemblyPath));

        await registry.SwapAsync("ext-mut", descriptor);
        var chain = await composer.GetInputChainAsync("agent-a");

        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "msg" };
        await chain[0].InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Properties.Should().ContainKey("result").WhoseValue.Should().Be("mutated",
            "property mutation must propagate back to the caller");
    }

    // C-4. Priority ordering: two handlers run in ascending priority order
    [Fact]
    public async Task CsharpHandler_Priority_LowerRunsFirst()
    {
        var (registry, composer) = MakeChain();

        var log = new List<string>();
        var bindFirst = new HandlerBinding("h-first", ExtensionSeams.AgentInput, 100, "log",
            new LoggingInputMiddleware(log, "first"));
        var bindSecond = new HandlerBinding("h-second", ExtensionSeams.AgentInput, 200, "log",
            new LoggingInputMiddleware(log, "second"));

        var descA = new ExtensionDescriptor("ext-a", "1.0.0", MakeManifest("ext-a", "1.0.0"),
            new[] { bindSecond }, new ExtensionAssemblyLoadContext(AssemblyPath));
        var descB = new ExtensionDescriptor("ext-b", "1.0.0", MakeManifest("ext-b", "1.0.0"),
            new[] { bindFirst }, new ExtensionAssemblyLoadContext(AssemblyPath));

        await registry.SwapAsync("ext-a", descA);
        await registry.SwapAsync("ext-b", descB);

        var chain = await composer.GetInputChainAsync("agent-a");
        chain.Should().HaveCount(2);

        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "msg" };
        foreach (var mw in chain)
            await mw.InvokeAsync(ctx, () => Task.CompletedTask);

        log.Should().ContainInOrder(new[] { "first", "second" }, "priority 100 must run before 200");
    }

    // ── In-process test doubles ────────────────────────────────────────────

    private sealed class NoOpInputMiddleware : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
            => next();
    }

    private sealed class ShortCircuitInputMiddleware : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class PropertySetterInputMiddleware(string key, string value) : AgentInputMiddleware
    {
        public override async Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
        {
            await next();
            ctx.Properties[key] = value;
        }
    }

    private sealed class LoggingInputMiddleware(List<string> log, string tag) : AgentInputMiddleware
    {
        public override async Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
        {
            log.Add(tag);
            await next();
        }
    }
}
