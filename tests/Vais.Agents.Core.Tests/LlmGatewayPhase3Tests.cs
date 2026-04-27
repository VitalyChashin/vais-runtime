// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// GW-19 — Phase 1 tests: <see cref="PassThroughIdentityResolver"/>,
/// <see cref="InMemoryModelRouter"/>, and their DI extensions.
/// </summary>
public sealed class LlmGatewayPhase3Tests
{
    // ── GW-17: PassThroughIdentityResolver ──────────────────────────────────

    [Fact]
    public async Task PassThrough_Returns_Configured_Context_Regardless_Of_Token()
    {
        var expected = new AgentContext() { WorkspaceId = "ws-1" };
        var sut = new PassThroughIdentityResolver(NullLogger<PassThroughIdentityResolver>.Instance, expected);

        var result = await sut.ResolveAsync("any-token");
        var result2 = await sut.ResolveAsync("different-token");

        result.Should().BeSameAs(expected);
        result2.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task PassThrough_Returns_Empty_Context_When_None_Configured()
    {
        var sut = new PassThroughIdentityResolver(NullLogger<PassThroughIdentityResolver>.Instance);

        var result = await sut.ResolveAsync("some-token");

        result.Should().BeSameAs(AgentContext.Empty);
    }

    [Fact]
    public async Task Custom_Resolver_Propagates_UnauthorizedAccessException()
    {
        IInboundIdentityResolver sut = new RejectingIdentityResolver();

        var act = () => sut.ResolveAsync("bad-token").AsTask();

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── GW-18: InMemoryModelRouter ───────────────────────────────────────────

    [Fact]
    public async Task InMemoryModelRouter_Resolves_Known_Alias()
    {
        var provider = new FakeCompletionProvider();
        var spec = new ModelSpec("openai", "gpt-4o");
        var router = BuildRouter(("gpt-4o", new ModelRoute(provider, spec)));

        var route = await router.ResolveAsync("gpt-4o");

        route.Provider.Should().BeSameAs(provider);
        route.ModelSpec.Should().Be(spec);
    }

    [Fact]
    public async Task InMemoryModelRouter_Resolves_Alias_Case_Insensitively()
    {
        var provider = new FakeCompletionProvider();
        var router = BuildRouter(("GPT-4O", new ModelRoute(provider, new ModelSpec("openai", "gpt-4o"))));

        var route = await router.ResolveAsync("gpt-4o");

        route.Provider.Should().BeSameAs(provider);
    }

    [Fact]
    public async Task InMemoryModelRouter_Throws_ModelNotFoundException_For_Unknown_Alias()
    {
        var router = BuildRouter(("gpt-4o", new ModelRoute(new FakeCompletionProvider(), new ModelSpec("openai", "gpt-4o"))));

        var act = () => router.ResolveAsync("claude-3-5-sonnet").AsTask();

        await act.Should().ThrowAsync<ModelNotFoundException>()
            .Where(ex => ex.ModelAlias == "claude-3-5-sonnet");
    }

    [Fact]
    public async Task InMemoryModelRouter_ListAliasesAsync_Returns_All_Configured_Aliases()
    {
        var router = BuildRouter(
            ("gpt-4o", new ModelRoute(new FakeCompletionProvider(), new ModelSpec("openai", "gpt-4o"))),
            ("gpt-4o-mini", new ModelRoute(new FakeCompletionProvider(), new ModelSpec("openai", "gpt-4o-mini"))));

        var aliases = await router.ListAliasesAsync();

        aliases.Should().BeEquivalentTo(["gpt-4o", "gpt-4o-mini"]);
    }

    [Fact]
    public void AddInMemoryModelRouter_Registers_IModelRouter_As_Singleton()
    {
        var provider = new FakeCompletionProvider();
        var services = new ServiceCollection();
        services.AddInMemoryModelRouter(routes =>
            routes.Add("gpt-4o", new ModelRoute(provider, new ModelSpec("openai", "gpt-4o"))));

        var sp = services.BuildServiceProvider();

        var router1 = sp.GetRequiredService<IModelRouter>();
        var router2 = sp.GetRequiredService<IModelRouter>();

        router1.Should().BeSameAs(router2);
        router1.Should().BeOfType<InMemoryModelRouter>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static InMemoryModelRouter BuildRouter(params (string alias, ModelRoute route)[] entries)
    {
        var dict = new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, route) in entries)
            dict[alias] = route;
        return new InMemoryModelRouter(dict);
    }

    private sealed class RejectingIdentityResolver : IInboundIdentityResolver
    {
        public ValueTask<AgentContext> ResolveAsync(string bearerToken, CancellationToken cancellationToken = default)
            => throw new UnauthorizedAccessException("Invalid token.");
    }
}
