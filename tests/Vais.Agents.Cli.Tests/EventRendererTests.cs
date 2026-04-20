// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Spectre.Console.Testing;
using Vais.Agents;
using Vais.Agents.Cli;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class EventRendererTests
{
    [Fact]
    public void Render_TurnStarted_PrintsGreenPrefix()
    {
        var console = new TestConsole();
        var renderer = new EventRenderer(console);
        var evt = new TurnStarted(DateTimeOffset.UtcNow, new AgentContext(), "hello");

        renderer.Render(evt);

        console.Output.Should().Contain("turn.started");
        console.Output.Should().Contain("hello");
    }

    [Fact]
    public void Render_TurnCompleted_IncludesTokenCountsAndDuration()
    {
        var console = new TestConsole();
        var renderer = new EventRenderer(console);
        var evt = new TurnCompleted(DateTimeOffset.UtcNow, new AgentContext(), "final text", "gpt-4", 100, 50, TimeSpan.FromMilliseconds(1200));

        renderer.Render(evt);

        console.Output.Should().Contain("turn.completed");
        console.Output.Should().Contain("100+50 tokens");
        console.Output.Should().Contain("1200ms");
    }

    [Fact]
    public void Render_CompletionDelta_AccumulatesUntilNonDeltaEvent()
    {
        var console = new TestConsole();
        var renderer = new EventRenderer(console);
        var context = new AgentContext();

        renderer.Render(new CompletionDelta(DateTimeOffset.UtcNow, context, "hello "));
        renderer.Render(new CompletionDelta(DateTimeOffset.UtcNow, context, "world"));
        // Non-delta event flushes the accumulated buffer.
        renderer.Render(new TurnCompleted(DateTimeOffset.UtcNow, context, "hello world", null, null, null, TimeSpan.FromMilliseconds(10)));

        console.Output.Should().Contain("hello world");
        console.Output.Should().Contain("turn.completed");
    }

    [Fact]
    public void Render_TurnFailed_UsesRedPrefix_AndCarriesErrorType()
    {
        var console = new TestConsole();
        var renderer = new EventRenderer(console);
        var evt = new TurnFailed(DateTimeOffset.UtcNow, new AgentContext(), "AgentPolicyDeniedException", "tenant mismatch", TimeSpan.Zero);

        renderer.Render(evt);

        console.Output.Should().Contain("turn.failed");
        console.Output.Should().Contain("AgentPolicyDeniedException");
        console.Output.Should().Contain("tenant mismatch");
    }

    [Fact]
    public void FlushAccumulated_NoDeltas_NoOp()
    {
        var console = new TestConsole();
        var renderer = new EventRenderer(console);

        renderer.FlushAccumulated();

        console.Output.Should().BeEmpty();
    }
}
