// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Runtime.Host.Tests;

/// <summary>
/// M2.1 DI resolution probe — asserts that <see cref="CompositionRoot.ConfigureServices"/>
/// can resolve every interface in the known-required contract list without starting a
/// silo, Docker container, or database.
///
/// Guards against the recurring failure shape: a new service is registered in
/// <c>AddInMemoryAgentRuntime</c> (used by tests/samples) but omitted from
/// <see cref="CompositionRoot.ConfigureServices"/> (used by the deployable container).
/// The result is a <c>ManifestInstantiationException</c> at first invoke in production.
///
/// When a new translator or runtime dependency lands, add one line to the contract
/// list below so the next omission fails here rather than in a live environment.
/// </summary>
public sealed class CompositionRootDiResolutionTests
{
    private static ServiceCollection BuildBaseline()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IGrainFactory>());
        services.AddSingleton(Substitute.For<IClusterClient>());
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vais:ContainerPlugin:CallTokenSecret"] = "test-secret-at-least-32-chars-long-xxxx",
                })
                .Build());
        return services;
    }

    // NOTE: the standalone Composition_Registers_BackgroundAgentTracker probe was folded into the
    // shared CriticalRuntimeContracts.All list (CriticalRuntimeContracts.cs) and is now covered by
    // CompositionRootTests.CriticalContracts_Verify_Passes_For_Full_CompositionRoot. Background-mode
    // LocalAgentRef requires IBackgroundAgentTracker at translation time; it went missing from
    // CompositionRoot on 2026-05-15 (ManifestInstantiationException on first invoke). The startup
    // self-check now catches that whole class of omission at boot rather than first request.

    [Fact]
    public void CompositionRoot_Covers_All_InMemoryAgentRuntime_ServiceTypes()
    {
        // M2.3 parity guard: every service type registered by AddInMemoryAgentRuntime (the
        // test/sample runtime) must also be registered by CompositionRoot (the production runtime).
        // Today that is only IAgentRuntime; as AddInMemoryAgentRuntime gains new registrations,
        // this test automatically catches any CompositionRoot omission before it reaches production.
        var inMemoryServices = new ServiceCollection();
        inMemoryServices.AddInMemoryAgentRuntime();
        var inMemoryTypes = inMemoryServices.Select(d => d.ServiceType).ToHashSet();

        var prodServices = BuildBaseline();
        CompositionRoot.ConfigureServices(prodServices, new RuntimeOptions());
        var prodTypes = prodServices.Select(d => d.ServiceType).ToHashSet();

        var missing = inMemoryTypes.Except(prodTypes).OrderBy(t => t.FullName).ToList();
        missing.Should().BeEmpty(
            because: "CompositionRoot must register every service that AddInMemoryAgentRuntime provides; " +
                     "add the missing registration(s) to CompositionRoot.ConfigureServices");
    }

    // Co-hosting regression: AddOrleansAgentRuntime registers OrleansAgentContextAccessor (silo-side,
    // reads Orleans RequestContext) as IAgentContextAccessor and wins the slot via first TryAdd. With
    // JWT on, the control plane pushes the authenticated principal onto AsyncLocalAgentContextAccessor
    // on the ingress thread, but the control-plane consumers (lifecycle managers, endpoint gate,
    // approval + MCP mutation handlers) resolve IAgentContextAccessor. Before IngressFirstAgentContextAccessor
    // that resolved to the Orleans accessor whose RequestContext is empty on the ingress thread, so every
    // authenticated apply synthesized an anonymous principal and RBAC denied it (NB-13 live failure).

    [Fact]
    public void Jwt_On_IAgentContextAccessor_Resolves_To_IngressFirst_Composite()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { JwtAuthority = "http://issuer.example/" });

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IAgentContextAccessor>().Should().BeOfType<IngressFirstAgentContextAccessor>(
            because: "the ingress-first composite must shadow the silo-side OrleansAgentContextAccessor " +
                     "so the HTTP/MCP principal reaches control-plane RBAC + audit.");
    }

    [Fact]
    public void IngressFirst_Surfaces_The_Pushed_Ingress_Principal()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { JwtAuthority = "http://issuer.example/" });

        using var sp = services.BuildServiceProvider();
        var accessor = sp.GetRequiredService<IAgentContextAccessor>();
        var ingress = sp.GetRequiredService<AsyncLocalAgentContextAccessor>();

        // No ingress push (e.g. a silo grain turn): falls back to the Orleans accessor, which reads an
        // empty RequestContext off the grain path → anonymous.
        accessor.Current.UserId.Should().BeNull();

        // An authenticated control-plane request pushes the principal onto the ingress AsyncLocal.
        using (ingress.Push(new AgentContext(UserId: "alice") { Scopes = new[] { "vais.author" } }))
        {
            accessor.Current.UserId.Should().Be("alice");
            accessor.Current.Scopes.Should().ContainSingle().Which.Should().Be("vais.author");
        }

        // Scope disposed → back to the silo fallback.
        accessor.Current.UserId.Should().BeNull();
    }
}
