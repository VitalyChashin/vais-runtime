// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Vais.Agents.Control;
using Xunit;
using Vais.Agents.Control.Policy.Opa;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Runtime.Host;

namespace Vais.Agents.Runtime.Host.Tests;

/// <summary>
/// Guard rails for the runtime-host composition root. These tests lock in the
/// ordering discipline that prevents the v0.11 footgun — if a future refactor
/// registers the generic control-plane wiring before the Orleans durability
/// sidecars, the InMemory defaults will silently win and these assertions fail.
/// </summary>
public class CompositionRootTests
{
    private static ServiceCollection BuildBaseline()
    {
        var services = new ServiceCollection();

        // Minimal ambient DI the composition root expects from the co-hosted silo.
        services.AddSingleton(Substitute.For<IGrainFactory>());
        services.AddSingleton(Substitute.For<IClusterClient>());
        services.AddLogging();

        return services;
    }

    [Fact]
    public void Composition_Registers_OrleansBacked_Idempotency_Store()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IIdempotencyStore>();

        store.Should().BeOfType<OrleansIdempotencyStore>(
            because: "AddOrleansIdempotencyStore must run before AddAgentControlPlaneIdempotency so TryAddSingleton picks Orleans, not InMemory — the v0.11 ordering footgun lives here.");
    }

    [Fact]
    public void Composition_Registers_OrleansBacked_Graph_Checkpointer()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        var checkpointer = sp.GetRequiredService<IGraphCheckpointer>();

        checkpointer.Should().BeOfType<OrleansCheckpointer>();
    }

    [Fact]
    public void Composition_Registers_OrleansBacked_Task_Store()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        var taskStore = sp.GetRequiredService<ITaskStore>();

        taskStore.Should().BeOfType<OrleansTaskStore>();
    }

    [Fact]
    public void Options_Localhost_Mode_Requires_No_Connection_Strings()
    {
        var options = new RuntimeOptions
        {
            Mode = "localhost",
            RedisConnection = null,
            PostgresConnection = null,
        };

        var act = () => options.EnsureValid();

        act.Should().NotThrow(
            because: "localhost mode uses memory grain storage + memory streams — no external deps needed.");
    }

    [Fact]
    public void Options_Clustered_Mode_Requires_Connection_String()
    {
        var options = new RuntimeOptions
        {
            Mode = "clustered",
            ClusteringBackend = "redis",
            RedisConnection = null,
        };

        var act = () => options.EnsureValid();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VAIS_REDIS_CONNECTION*required*clustered*",
                because: "startup must fail loudly with an actionable message, not silently fall back.");
    }

    [Fact]
    public void Composition_OpaEngine_Registered_When_BaseUrl_Set()
    {
        var services = BuildBaseline();
        var options = new RuntimeOptions
        {
            OpaBaseUrl = "http://opa:8181",
            OpaFailMode = OpaFailMode.Closed,
        };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();
        var engine = sp.GetRequiredService<IAgentPolicyEngine>();

        engine.Should().BeOfType<OpaPolicyEngine>(
            because: "a non-empty OPA base URL must swap the default AllowAll (NullAgentPolicyEngine) for the real OpaPolicyEngine.");
    }

    [Fact]
    public void Composition_NoOpa_Falls_Back_To_AllowAll()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { OpaBaseUrl = null });

        using var sp = services.BuildServiceProvider();

        // Without OPA configured, no IAgentPolicyEngine is registered — AgentLifecycleManager
        // falls back to NullAgentPolicyEngine.Instance internally. The host startup banner
        // prints "opa=disabled (AllowAll)" so the default-open behaviour is never silent.
        var engine = sp.GetService<IAgentPolicyEngine>();
        engine.Should().BeNull(
            because: "AgentLifecycleManager applies NullAgentPolicyEngine.Instance when the DI lookup returns null; explicit registration would be a footgun.");
    }
}
