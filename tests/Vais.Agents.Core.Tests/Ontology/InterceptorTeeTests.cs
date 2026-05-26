// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C1-4 verify: the observability producer seam exists, the default null-tee drops events
/// silently, and a custom observability interceptor that emits to the tee runs on a substrate
/// chain (north-shape and south-shape) without altering the outcome.
/// </summary>
public sealed class InterceptorTeeTests
{
    [Fact]
    public async Task NullInterceptorTee_DropsEventsAndReturnsCompletedValueTask()
    {
        var ev = new InterceptorTeeEvent
        {
            EventName = "x",
            Context = new TestContext { Operation = OntologyOperation.Call, AgentContext = AgentContext.Empty },
        };

        var t = NullInterceptorTee.Instance.EmitAsync(ev);

        t.IsCompletedSuccessfully.Should().BeTrue();
        await t;
    }

    [Fact]
    public async Task ObservabilityInterceptor_EmittingToTee_DoesNotAlterChainOutcome()
    {
        var tee = new RecordingTee();
        var ctx = new TestContext { Operation = OntologyOperation.Call, AgentContext = AgentContext.Empty };
        var observer = new TeeingObserver(tee, "south.call");
        var interceptors = new OntologyInterceptor<TestContext, string>[] { observer };

        var chain = OntologyInterceptorChain.Compose(interceptors, ctx, () => Task.FromResult("upstream"));
        var result = await chain();

        result.Should().Be("upstream");
        tee.Events.Should().HaveCount(1);
        tee.Events[0].EventName.Should().Be("south.call");
        tee.Events[0].Context.Should().BeSameAs(ctx);
    }

    [Fact]
    public async Task ObservabilityInterceptor_RunsCleanlyOnListOperationToo()
    {
        var tee = new RecordingTee();
        var ctx = new TestContext { Operation = OntologyOperation.List, AgentContext = AgentContext.Empty };
        var observer = new TeeingObserver(tee, "north.list");
        var interceptors = new OntologyInterceptor<TestContext, string>[] { observer };

        var chain = OntologyInterceptorChain.Compose(interceptors, ctx, () => Task.FromResult("listing"));
        var result = await chain();

        result.Should().Be("listing");
        tee.Events.Should().ContainSingle()
            .Which.Context.Operation.Should().Be(OntologyOperation.List);
    }

    [Fact]
    public void TeeingObserver_IsObservabilityKindByDefault()
    {
        var observer = new TeeingObserver(new RecordingTee(), "x");
        ((OntologyInterceptor)observer).Kind.Should().Be(InterceptorKind.Observability);
    }

    private sealed class TestContext : InterceptionContext;

    private sealed class RecordingTee : IInterceptorTee
    {
        public List<InterceptorTeeEvent> Events { get; } = [];

        public ValueTask EmitAsync(InterceptorTeeEvent teeEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(teeEvent);
            return default;
        }
    }

    private sealed class TeeingObserver(IInterceptorTee tee, string eventName)
        : OntologyInterceptor<TestContext, string>
    {
        public override async Task<string> InvokeAsync(
            TestContext context, Func<Task<string>> next, CancellationToken cancellationToken = default)
        {
            var result = await next().ConfigureAwait(false);
            await tee.EmitAsync(
                new InterceptorTeeEvent { EventName = eventName, Context = context, Payload = result },
                cancellationToken).ConfigureAwait(false);
            return result;
        }
    }
}
