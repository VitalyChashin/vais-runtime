// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

public sealed class OntologyInterceptorChainTests
{
    // ── chain ordering (request + response) ────────────────────────────────

    [Fact]
    public async Task Compose_RunsRequestPhaseOuterToInnerAndResponsePhaseInnerToOuter()
    {
        var log = new List<string>();
        var ctx = NewContext();
        var interceptors = new OntologyInterceptor<TestContext, string>[]
        {
            new RecordingInterceptor("A", log),
            new RecordingInterceptor("B", log),
            new RecordingInterceptor("C", log),
        };

        var chain = OntologyInterceptorChain.Compose(interceptors, ctx, () =>
        {
            log.Add("terminal");
            return Task.FromResult("terminal");
        });
        var result = await chain();

        result.Should().Be("terminal");
        log.Should().Equal(
            "A:request",
            "B:request",
            "C:request",
            "terminal",
            "C:response",
            "B:response",
            "A:response");
    }

    // ── short-circuit ──────────────────────────────────────────────────────

    [Fact]
    public async Task Compose_ShortCircuitInterceptorPreventsDownstreamAndTerminal()
    {
        var log = new List<string>();
        var ctx = NewContext();
        var interceptors = new OntologyInterceptor<TestContext, string>[]
        {
            new RecordingInterceptor("outer", log),
            new ShortCircuitInterceptor("denied"),
            new RecordingInterceptor("never", log),
        };
        var terminalRan = false;

        var chain = OntologyInterceptorChain.Compose(interceptors, ctx, () =>
        {
            terminalRan = true;
            return Task.FromResult("terminal");
        });
        var result = await chain();

        result.Should().Be("denied");
        terminalRan.Should().BeFalse();
        log.Should().Equal("outer:request", "outer:response");
    }

    // ── phase metadata ─────────────────────────────────────────────────────

    [Fact]
    public void OntologyInterceptor_DefaultsToObservabilityKindAndBothPhase()
    {
        var i = new RecordingInterceptor("x", new List<string>());

        ((OntologyInterceptor)i).Kind.Should().Be(InterceptorKind.Observability);
        ((OntologyInterceptor)i).Phase.Should().Be(InterceptorPhase.Both);
    }

    [Fact]
    public void OntologyInterceptor_DerivedClassesCanOverrideKindAndPhase()
    {
        var v = new ValidationOnlyInterceptor();

        ((OntologyInterceptor)v).Kind.Should().Be(InterceptorKind.Validation);
        ((OntologyInterceptor)v).Phase.Should().Be(InterceptorPhase.Request);
    }

    [Fact]
    public void InterceptorPhase_BothEqualsRequestPlusResponse()
    {
        (InterceptorPhase.Request | InterceptorPhase.Response).Should().Be(InterceptorPhase.Both);
    }

    // ── context carries binding ────────────────────────────────────────────

    [Fact]
    public async Task Compose_PassesContextWithBindingToEveryInterceptor()
    {
        var binding = new FakeBinding("ont-v7");
        var ctx = new TestContext
        {
            Operation = OntologyOperation.List,
            AgentContext = new AgentContext(AgentName: "test-agent"),
            Binding = binding,
        };

        TestContext? observed = null;
        var interceptors = new OntologyInterceptor<TestContext, string>[]
        {
            new CapturingInterceptor(c => observed = c),
        };
        var chain = OntologyInterceptorChain.Compose(interceptors, ctx, () => Task.FromResult("ok"));
        await chain();

        observed.Should().BeSameAs(ctx);
        observed!.Binding.Should().BeSameAs(binding);
        observed.Binding!.OntologyVersion.Should().Be("ont-v7");
        observed.Operation.Should().Be(OntologyOperation.List);
    }

    // ── argument guards ────────────────────────────────────────────────────

    [Fact]
    public void Compose_ThrowsOnNullInterceptors()
    {
        var act = () => OntologyInterceptorChain.Compose<TestContext, string>(
            interceptors: null!,
            context: NewContext(),
            terminal: () => Task.FromResult(""));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compose_ThrowsOnNullContext()
    {
        var act = () => OntologyInterceptorChain.Compose<TestContext, string>(
            interceptors: [],
            context: null!,
            terminal: () => Task.FromResult(""));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Compose_ThrowsOnNullTerminal()
    {
        var act = () => OntologyInterceptorChain.Compose<TestContext, string>(
            interceptors: [],
            context: NewContext(),
            terminal: null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Compose_EmptyInterceptorListInvokesTerminalDirectly()
    {
        var chain = OntologyInterceptorChain.Compose<TestContext, string>(
            interceptors: [],
            context: NewContext(),
            terminal: () => Task.FromResult("only-terminal"));

        (await chain()).Should().Be("only-terminal");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static TestContext NewContext() => new()
    {
        Operation = OntologyOperation.Call,
        AgentContext = new AgentContext("test-agent"),
    };

    private sealed class TestContext : InterceptionContext;

    private sealed class FakeBinding(string version) : IOntologyBinding
    {
        public string OntologyVersion { get; } = version;
    }

    private sealed class RecordingInterceptor(string name, List<string> log)
        : OntologyInterceptor<TestContext, string>
    {
        public override async Task<string> InvokeAsync(
            TestContext context, Func<Task<string>> next, CancellationToken cancellationToken = default)
        {
            log.Add($"{name}:request");
            try
            {
                return await next().ConfigureAwait(false);
            }
            finally
            {
                log.Add($"{name}:response");
            }
        }
    }

    private sealed class ShortCircuitInterceptor(string synthetic)
        : OntologyInterceptor<TestContext, string>
    {
        public override Task<string> InvokeAsync(
            TestContext context, Func<Task<string>> next, CancellationToken cancellationToken = default)
            => Task.FromResult(synthetic);
    }

    private sealed class CapturingInterceptor(Action<TestContext> capture)
        : OntologyInterceptor<TestContext, string>
    {
        public override Task<string> InvokeAsync(
            TestContext context, Func<Task<string>> next, CancellationToken cancellationToken = default)
        {
            capture(context);
            return next();
        }
    }

    private sealed class ValidationOnlyInterceptor : OntologyInterceptor<TestContext, string>
    {
        public override InterceptorKind Kind => InterceptorKind.Validation;
        public override InterceptorPhase Phase => InterceptorPhase.Request;
    }
}
