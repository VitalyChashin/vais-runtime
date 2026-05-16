// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class AgentInputMiddlewareTests
{
    // ── chain composition ──────────────────────────────────────────────────

    [Fact]
    public async Task Chain_ExecutesMiddlewareOutermostFirst()
    {
        var order = new List<string>();
        var ctx = new AgentInputContext { AgentId = "a", Message = "hello" };
        var mw = new[]
        {
            new RecordingMiddleware("first", order),
            new RecordingMiddleware("second", order),
            new RecordingMiddleware("third", order),
        };

        await RunChain(ctx, mw);

        order.Should().Equal("first", "second", "third");
    }

    [Fact]
    public async Task Chain_MutatesMessageAcrossMiddleware()
    {
        var ctx = new AgentInputContext { AgentId = "a", Message = "hello" };
        var mw = new AgentInputMiddleware[]
        {
            new AppendingMiddleware(" world"),
            new AppendingMiddleware("!"),
        };

        await RunChain(ctx, mw);

        ctx.Message.Should().Be("hello world!");
    }

    [Fact]
    public async Task Chain_ShortCircuits_WhenNextNotCalled()
    {
        var reached = false;
        var ctx = new AgentInputContext { AgentId = "a", Message = "original" };
        var mw = new AgentInputMiddleware[]
        {
            new OverrideMiddleware("blocked"),
            new CallbackMiddleware(_ => reached = true),
        };

        await RunChain(ctx, mw);

        reached.Should().BeFalse("downstream middleware must not run when next is not called");
        ctx.Message.Should().Be("blocked");
    }

    [Fact]
    public async Task Chain_PassThrough_WhenNoMiddleware()
    {
        var ctx = new AgentInputContext { AgentId = "a", Message = "untouched" };
        await RunChain(ctx, Array.Empty<AgentInputMiddleware>());
        ctx.Message.Should().Be("untouched");
    }

    // ── factory ───────────────────────────────────────────────────────────

    [Fact]
    public void DefaultFactory_Throws_ForUnknownName()
    {
        var factory = new DefaultAgentInputMiddlewareFactory(
            Array.Empty<NamedAgentInputMiddlewareRegistration>(),
            new ServiceCollection().BuildServiceProvider());

        var spec = new GatewayMiddlewareSpec("unknown");
        var act = () => factory.Create(spec);
        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown*");
    }

    [Fact]
    public void DefaultFactory_ReturnsMiddleware_ForKnownName()
    {
        var registration = new NamedAgentInputMiddlewareRegistration(
            "test-mw",
            (_, _) => new AppendingMiddleware(" via factory"));

        var factory = new DefaultAgentInputMiddlewareFactory(
            [registration],
            new ServiceCollection().BuildServiceProvider());

        var mw = factory.Create(new GatewayMiddlewareSpec("test-mw"));
        mw.Should().BeOfType<AppendingMiddleware>();
    }

    [Fact]
    public void DefaultFactory_MatchesNameCaseInsensitively()
    {
        var registration = new NamedAgentInputMiddlewareRegistration(
            "MyMiddleware",
            (_, _) => new AppendingMiddleware("x"));

        var factory = new DefaultAgentInputMiddlewareFactory(
            [registration],
            new ServiceCollection().BuildServiceProvider());

        var act = () => factory.Create(new GatewayMiddlewareSpec("mymiddleware"));
        act.Should().NotThrow();
    }

    // ── DI extensions ─────────────────────────────────────────────────────

    [Fact]
    public void AddAgentInputMiddleware_RegistersSingletonMiddleware()
    {
        var services = new ServiceCollection();
        services.AddAgentInputMiddleware<NoOpMiddleware>();
        var sp = services.BuildServiceProvider();

        var resolved = sp.GetServices<AgentInputMiddleware>();
        resolved.Should().ContainSingle(m => m is NoOpMiddleware);
    }

    [Fact]
    public void AddDefaultAgentInputMiddlewareFactory_RegistersFactory()
    {
        var services = new ServiceCollection();
        services.AddDefaultAgentInputMiddlewareFactory();
        var sp = services.BuildServiceProvider();

        sp.GetService<IAgentInputMiddlewareFactory>().Should().BeOfType<DefaultAgentInputMiddlewareFactory>();
    }

    [Fact]
    public void AddDefaultAgentInputMiddlewareFactory_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddDefaultAgentInputMiddlewareFactory();
        services.AddDefaultAgentInputMiddlewareFactory();
        var sp = services.BuildServiceProvider();

        sp.GetServices<IAgentInputMiddlewareFactory>().Should().HaveCount(1);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static Task RunChain(AgentInputContext ctx, IReadOnlyList<AgentInputMiddleware> middleware)
    {
        Func<Task> chain = () => Task.CompletedTask;
        for (var i = middleware.Count - 1; i >= 0; i--)
        {
            var mw = middleware[i];
            var inner = chain;
            chain = () => mw.InvokeAsync(ctx, inner, CancellationToken.None);
        }
        return chain();
    }

    private sealed class NoOpMiddleware : AgentInputMiddleware { }

    private sealed class RecordingMiddleware(string name, List<string> order) : AgentInputMiddleware
    {
        public override async Task InvokeAsync(AgentInputContext context, Func<Task> next, CancellationToken cancellationToken = default)
        {
            order.Add(name);
            await next();
        }
    }

    private sealed class AppendingMiddleware(string suffix) : AgentInputMiddleware
    {
        public override async Task InvokeAsync(AgentInputContext context, Func<Task> next, CancellationToken cancellationToken = default)
        {
            context.Message += suffix;
            await next();
        }
    }

    private sealed class OverrideMiddleware(string newMessage) : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext context, Func<Task> next, CancellationToken cancellationToken = default)
        {
            context.Message = newMessage;
            return Task.CompletedTask; // intentionally does NOT call next
        }
    }

    private sealed class CallbackMiddleware(Action<AgentInputContext> callback) : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext context, Func<Task> next, CancellationToken cancellationToken = default)
        {
            callback(context);
            return next();
        }
    }
}
