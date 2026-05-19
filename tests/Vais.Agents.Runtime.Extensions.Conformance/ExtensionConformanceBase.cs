// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Conformance;

/// <summary>
/// Registry-and-composer conformance assertions that are host-agnostic.
/// Tests here do not invoke handlers; they verify that scope, priority, remove, and swap
/// all behave the same regardless of which host (csharp, container, …) produced the descriptor.
/// </summary>
/// <remarks>
/// Host-specific invocation semantics (passthrough, shortCircuit, contextPatch mutation)
/// are tested in the host-specific subclass rather than here, because those semantics
/// require host-aware test fixtures.
/// </remarks>
public abstract class ExtensionConformanceBase
{
    // ── Abstract factory ───────────────────────────────────────────────────

    /// <summary>
    /// Return a descriptor with one agentInput handler at the given priority.
    /// The handler's behaviour is irrelevant for the base-class tests — only
    /// its presence in the chain matters.
    /// </summary>
    protected abstract Task<ExtensionDescriptor> CreateDescriptorAsync(
        string extensionId, ExtensionScope? scope, int priority);

    // ── Helpers ────────────────────────────────────────────────────────────

    protected static ExtensionManifest MakeManifest(string id, string version, ExtensionScope? scope = null) =>
        new(Id: id, Version: version,
            Spec: new ExtensionSpec
            {
                Host = "csharp",
                Handlers = new List<ExtensionHandler> { new() { Id = $"{id}-in", Seam = ExtensionSeams.AgentInput } },
                Scope = scope
            },
            Labels: null, Description: null);

    internal static (ExtensionHandlerRegistry Registry, DefaultExtensionChainComposer Composer) MakeChain()
    {
        var registry = new ExtensionHandlerRegistry();
        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);
        return (registry, composer);
    }

    // ── Conformance tests ──────────────────────────────────────────────────

    // 1. Registration: descriptor appears in chain after swap
    [Fact]
    public async Task Registration_AfterSwap_HandlerAppearsInChain()
    {
        var (registry, composer) = MakeChain();
        var descriptor = await CreateDescriptorAsync("ext-reg", scope: null, priority: 100);
        await registry.SwapAsync("ext-reg", descriptor);

        var chain = await composer.GetInputChainAsync("any-agent");
        chain.Should().ContainSingle("registered handler must appear in the chain");
    }

    // 2. Scope: cluster-wide (null) matches every agent
    [Fact]
    public async Task Scope_ClusterWide_MatchesAllAgents()
    {
        var (registry, composer) = MakeChain();
        var descriptor = await CreateDescriptorAsync("ext-cw", scope: null, priority: 100);
        await registry.SwapAsync("ext-cw", descriptor);

        var chainA = await composer.GetInputChainAsync("agent-alpha");
        var chainB = await composer.GetInputChainAsync("agent-beta");

        chainA.Should().ContainSingle("cluster-wide extension must bind to every agent");
        chainB.Should().ContainSingle("cluster-wide extension must bind to every agent");
    }

    // 3. Scope: agentIds filter excludes non-listed agents
    [Fact]
    public async Task Scope_AgentIdFilter_ExcludesNonListed()
    {
        var (registry, composer) = MakeChain();
        var scope = new ExtensionScope(Workspaces: null, AgentIds: new List<string> { "target-agent" }, Selector: null);
        var descriptor = await CreateDescriptorAsync("ext-scoped", scope, priority: 100);
        await registry.SwapAsync("ext-scoped", descriptor);

        var inChain  = await composer.GetInputChainAsync("target-agent");
        var outChain = await composer.GetInputChainAsync("other-agent");

        inChain.Should().ContainSingle("listed agent must have handler");
        outChain.Should().BeEmpty("unlisted agent must have empty chain");
    }

    // 4. Priority ordering: lower value sorts first in the chain
    [Fact]
    public async Task Priority_LowerValueSortsFirst()
    {
        var (registry, composer) = MakeChain();
        var descLow  = await CreateDescriptorAsync("ext-low",  scope: null, priority: 100);
        var descHigh = await CreateDescriptorAsync("ext-high", scope: null, priority: 200);

        await registry.SwapAsync("ext-low",  descLow);
        await registry.SwapAsync("ext-high", descHigh);

        var chain = await composer.GetInputChainAsync("agent-a");
        chain.Should().HaveCount(2);

        // Inspect the underlying bindings via the registry snapshot to verify order.
        var snapshot = registry.Snapshot();
        var allBindings = new[]
        {
            snapshot["ext-low"].Handlers[0],
            snapshot["ext-high"].Handlers[0],
        }.OrderBy(b => b.Priority).ToArray();

        allBindings[0].Priority.Should().BeLessThan(allBindings[1].Priority,
            "lower priority value must come first in sorted order");
    }

    // 5. Remove: handler absent from chain after removal
    [Fact]
    public async Task Remove_ClearsHandlerFromChain()
    {
        var (registry, composer) = MakeChain();
        var descriptor = await CreateDescriptorAsync("ext-rem", scope: null, priority: 100);
        await registry.SwapAsync("ext-rem", descriptor);

        (await composer.GetInputChainAsync("agent-a")).Should().ContainSingle();

        await registry.RemoveAsync("ext-rem");
        composer.InvalidateAll();

        (await composer.GetInputChainAsync("agent-a")).Should().BeEmpty("handler must be absent after remove");
    }

    // 6. Swap: only the new descriptor's handler is in the chain
    [Fact]
    public async Task Swap_ReplacesOldDescriptor()
    {
        var (registry, composer) = MakeChain();
        var v1 = await CreateDescriptorAsync("ext-swap", scope: null, priority: 100);
        var v2 = await CreateDescriptorAsync("ext-swap", scope: null, priority: 100);

        await registry.SwapAsync("ext-swap", v1);
        var replaced = await registry.SwapAsync("ext-swap", v2);
        composer.InvalidateAll();

        replaced.Should().NotBeNull("swap must return the previous descriptor");
        replaced!.Version.Should().Be(v1.Version);

        var chain = await composer.GetInputChainAsync("agent-a");
        chain.Should().ContainSingle("only the v2 handler must be in the chain after swap");
    }
}
