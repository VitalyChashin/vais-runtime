// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C2-2 verify gate: the middleware resolves the coordinator's capability map and surfaces
/// it (a) structured under <see cref="AgentInputContext.Properties"/> and (b) prepended to
/// <see cref="AgentInputContext.Message"/> in-band so existing agents pick it up without
/// code change. Pass-through when the coordinator has no sub-agents.
/// </summary>
public sealed class CapabilityMapInputMiddlewareTests
{
    private static readonly CapabilityMap NonEmpty = new("coord", [
        new SubAgentCapability("reviewer", "code-reviewer", "Reviews code.", ["role:review"], LocalAgentInvocationMode.Blocking),
        new SubAgentCapability("tester", "test-runner", "Runs tests.", ["role:verify"], LocalAgentInvocationMode.Blocking),
    ]);

    [Fact]
    public async Task Invoke_WritesCapabilityMapIntoProperties()
    {
        var mw = new CapabilityMapInputMiddleware(new FakeBuilder(NonEmpty));
        var ctx = new AgentInputContext { AgentId = "coord", Message = "hello" };
        var nextCalled = false;

        await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();
        ctx.Properties.Should().ContainKey(CapabilityMapInputMiddleware.ContextPropertyKey);
        ctx.Properties[CapabilityMapInputMiddleware.ContextPropertyKey].Should().BeSameAs(NonEmpty);
    }

    [Fact]
    public async Task Invoke_PrependsMapTextOntoMessageByDefault()
    {
        var mw = new CapabilityMapInputMiddleware(new FakeBuilder(NonEmpty));
        var ctx = new AgentInputContext { AgentId = "coord", Message = "Please plan." };

        await mw.InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Message.Should().StartWith("Your team");
        ctx.Message.Should().Contain("- reviewer:");
        ctx.Message.Should().Contain("- tester:");
        ctx.Message.Should().EndWith("Please plan.");
    }

    [Fact]
    public async Task Invoke_SkipsMessageInjectionWhenOptedOut()
    {
        var mw = new CapabilityMapInputMiddleware(new FakeBuilder(NonEmpty),
            new CapabilityMapInputMiddlewareOptions { InjectIntoMessage = false });
        var ctx = new AgentInputContext { AgentId = "coord", Message = "Please plan." };

        await mw.InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Message.Should().Be("Please plan.",
            "InjectIntoMessage=false keeps the user message clean; consumer reads Properties only");
        ctx.Properties.Should().ContainKey(CapabilityMapInputMiddleware.ContextPropertyKey);
    }

    [Fact]
    public async Task Invoke_EmptyMapIsPassThroughForMessage()
    {
        var emptyMap = CapabilityMap.Empty("lonely");
        var mw = new CapabilityMapInputMiddleware(new FakeBuilder(emptyMap));
        var ctx = new AgentInputContext { AgentId = "lonely", Message = "hi" };

        await mw.InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Message.Should().Be("hi", "no sub-agents ⇒ no in-band text injection (still Properties-populated)");
        ctx.Properties[CapabilityMapInputMiddleware.ContextPropertyKey].Should().BeSameAs(emptyMap);
    }

    [Fact]
    public async Task Invoke_EmptyOriginalMessage_ResultsInMapTextAlone()
    {
        var mw = new CapabilityMapInputMiddleware(new FakeBuilder(NonEmpty));
        var ctx = new AgentInputContext { AgentId = "coord", Message = string.Empty };

        await mw.InvokeAsync(ctx, () => Task.CompletedTask);

        ctx.Message.Should().StartWith("Your team");
        ctx.Message.Should().NotContain("\n\n", "no double-newline when original message is empty");
    }

    [Fact]
    public void Constructor_RejectsNullBuilder()
    {
        FluentActions.Invoking(() => new CapabilityMapInputMiddleware(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Invoke_RejectsNullContextOrNext()
    {
        var mw = new CapabilityMapInputMiddleware(new FakeBuilder(NonEmpty));
        await FluentActions.Invoking(() => mw.InvokeAsync(null!, () => Task.CompletedTask)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Invoking(() => mw.InvokeAsync(new AgentInputContext { AgentId = "x", Message = "" }, null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    private sealed class FakeBuilder(CapabilityMap fixedMap) : IAgentCapabilityMapBuilder
    {
        public ValueTask<CapabilityMap> BuildAsync(string coordinatorAgentId, CancellationToken cancellationToken = default)
            => new(fixedMap);
        public void Invalidate(string coordinatorAgentId) { }
    }
}
