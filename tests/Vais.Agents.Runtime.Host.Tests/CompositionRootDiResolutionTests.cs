// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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

    [Fact]
    public void Composition_Registers_BackgroundAgentTracker()
    {
        // IBackgroundAgentTracker was missing from CompositionRoot on 2026-05-15.
        // Result: ManifestInstantiationException on the first invoke of any Background-mode agent.
        // This test is the CI anchor that prevents the same omission recurring.
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());
        using var sp = services.BuildServiceProvider();

        // IBackgroundAgentTracker is in the Vais.Agents namespace (parent of this test namespace).
        sp.GetRequiredService<IBackgroundAgentTracker>().Should().NotBeNull(
            because: "Background-mode LocalAgentRef requires IBackgroundAgentTracker at manifest translation time");
    }

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
}
