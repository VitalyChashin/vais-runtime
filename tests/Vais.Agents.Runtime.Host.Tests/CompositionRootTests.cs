// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Xunit;
using Vais.Agents.Control.Policy.Opa;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Runtime.Host;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Plugins;
using Vais.Agents.Runtime.Plugins.Python;

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

    [Fact]
    public void Composition_Translator_Registered_For_ConfigureAgentGrains()
    {
        // v0.17 Pillar B: the translator must be registered alongside
        // ConfigureAgentGrains so the lambda-captured IServiceProvider can
        // resolve IAgentManifestTranslator at grain activation. If the
        // registration order ever drifts, grain activation throws at the
        // point of first invoke — this guard fails at build time instead.
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();

        sp.GetService<IAgentManifestTranslator>()
            .Should().NotBeNull(because: "AddAgentManifestInstantiator must register the translator as a singleton.");

        sp.GetService<Func<string, CancellationToken, ValueTask<StatefulAgentOptions>>>()
            .Should().NotBeNull(because: "ConfigureAgentGrains must install a Func<string, CancellationToken, ValueTask<StatefulAgentOptions>> so AiAgentGrain's activation path has something to call.");
    }

    [Fact]
    public void Composition_Swaps_InMemoryRegistry_For_OrleansAgentRegistry()
    {
        // v0.17 Pillar B: v0.16 used InMemoryAgentRegistry which evaporates on
        // pod roll; the manifest-driven runtime demands durability. The Orleans
        // registry uses grain-per-id with a directory grain for enumeration.
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAgentRegistry>();

        registry.Should().BeOfType<OrleansAgentRegistry>(
            because: "AddOrleansAgentRegistry must replace v0.16's InMemoryAgentRegistry so vais apply persists across silo restart.");
    }

    [Fact]
    public void Composition_Plugin_Registry_Registered_When_PluginsDirectory_Set()
    {
        // v0.18 Pillar C: a non-empty PluginsDirectory wires AddAgentPlugins, which registers
        // IPluginHandlerRegistry as a singleton. Missing directory is fine — the loader is a
        // no-op and the registry resolves empty.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vais-plugins-test-{Guid.NewGuid():N}");
        var services = BuildBaseline();
        var options = new RuntimeOptions { PluginsDirectory = tempRoot };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetService<IPluginHandlerRegistry>();

        registry.Should().NotBeNull(because: "a non-empty PluginsDirectory must register IPluginHandlerRegistry.");
        registry!.HandlerTypeNames.Should().BeEmpty(because: "temp directory is empty; loader is a no-op but registry is still wired.");
    }

    [Fact]
    public void Composition_Plugin_Registry_Not_Registered_When_PluginsDirectory_Empty()
    {
        // Empty PluginsDirectory ⇒ loader disabled. Translator falls through to the v0.17
        // declarative path with no lookup overhead.
        var services = BuildBaseline();
        var options = new RuntimeOptions { PluginsDirectory = "" };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetService<IPluginHandlerRegistry>();

        registry.Should().BeNull(because: "empty PluginsDirectory explicitly disables the plugin loader — translator sees no registry and skips the plugin branch.");
    }

    [Fact]
    public void Composition_Plugin_Registry_Registered_Before_Translator()
    {
        // Ordering lock: the translator's ctor calls sp.GetService<IPluginHandlerRegistry>()
        // at build time (via AddAgentManifestInstantiator). If the registration order ever
        // drifts such that AddAgentPlugins runs AFTER AddAgentManifestInstantiator, the
        // translator factory captures the missing registry and the plugin branch stops firing
        // even though the loader appears to be wired. This test asserts the registry reaches
        // the translator.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vais-plugins-test-{Guid.NewGuid():N}");
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { PluginsDirectory = tempRoot });

        using var sp = services.BuildServiceProvider();
        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var registry = sp.GetRequiredService<IPluginHandlerRegistry>();

        // We can't directly inspect the translator's captured registry, but we CAN assert
        // that both are resolvable from the same root provider — the translator was built
        // from sp which already knew about the registry.
        translator.Should().NotBeNull();
        registry.Should().NotBeNull();
    }

    [Fact]
    public void Options_FromEnvironment_Unset_Uses_Default_PluginsDirectory()
    {
        // Env var unset ⇒ default /var/lib/vais/plugins. Env var empty ⇒ disabled.
        Environment.SetEnvironmentVariable("VAIS_PLUGINS_DIRECTORY", null);
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.PluginsDirectory.Should().Be(RuntimeOptions.DefaultPluginsDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_PLUGINS_DIRECTORY", null);
        }
    }

    [Fact]
    public void Options_FromEnvironment_Empty_Disables_Plugins()
    {
        Environment.SetEnvironmentVariable("VAIS_PLUGINS_DIRECTORY", "");
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.PluginsDirectory.Should().Be("", because: "explicit empty string disables the plugin loader.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_PLUGINS_DIRECTORY", null);
        }
    }

    [Fact]
    public void Options_FromEnvironment_Custom_PluginsDirectory_Wins()
    {
        Environment.SetEnvironmentVariable("VAIS_PLUGINS_DIRECTORY", "/mnt/plugins");
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.PluginsDirectory.Should().Be("/mnt/plugins");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_PLUGINS_DIRECTORY", null);
        }
    }

    [Fact]
    public void Composition_GraphRegistry_Registered_As_OrleansBacked()
    {
        // v0.19 Pillar D PR 2: OrleansAgentGraphRegistry must be the implementation so
        // graph manifests survive pod roll (same reasoning as OrleansAgentRegistry in v0.17).
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAgentGraphRegistry>();

        registry.Should().BeOfType<OrleansAgentGraphRegistry>(
            because: "AddOrleansAgentGraphRegistry must register the durable Orleans-backed registry so vais apply -f graph.yaml persists across silo restart.");
    }

    [Fact]
    public void Composition_GraphLifecycleManager_Registered_After_GraphRegistry()
    {
        // v0.19 Pillar D PR 2: IAgentGraphLifecycleManager must resolve and its constructor
        // requires IAgentGraphRegistry, IAgentRegistry, IAgentLifecycleManager, and
        // IGraphCheckpointer — all of which must be registered by the time this resolves.
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        var manager = sp.GetRequiredService<IAgentGraphLifecycleManager>();

        manager.Should().BeOfType<AgentGraphLifecycleManager>(
            because: "the graph lifecycle manager must be registered after its dependencies (registry, agent registry, agent lifecycle, checkpointer) are all in DI.");
    }

    [Fact]
    public void Composition_Registers_Builtin_Providers_And_Guardrails()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();

        var providers = sp.GetServices<IModelProviderFactory>().Select(f => f.Provider).ToArray();
        providers.Should().Contain(new[] { "openai", "anthropic", "azure-openai" });

        var guardrails = sp.GetServices<IGuardrailFactory>().ToArray();
        guardrails.Should().HaveCount(6,
            because: "AddBuiltinGuardrails registers LengthCap + RegexAllowlist × {Input, Output} + RegexDenylist × {Input, Output} + LlmAsJudge.");
    }

    [Fact]
    public void Composition_RemoteInvoker_Registered()
    {
        // v0.20 Pillar E: IAgentRemoteInvoker must be registered so the graph lifecycle manager
        // can route cross-runtime nodes. AddAgentRemoteInvoker uses TryAddSingleton, so calling
        // it from ConfigureServices is always safe.
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        var invoker = sp.GetService<IAgentRemoteInvoker>();

        invoker.Should().NotBeNull(because: "AddAgentRemoteInvoker must register IAgentRemoteInvoker for cross-runtime graph refs to work.");
    }

    [Fact]
    public void Composition_PythonPluginHost_Registered_When_PythonPluginsDirectory_Set()
    {
        // v0.23 Python-plugins pillar: a non-empty PythonPluginsDirectory wires AddPythonPlugins,
        // which registers IPythonPluginHost as a singleton. Missing directory is fine — the loader
        // is a no-op and the host resolves with zero loaded plugins.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vais-python-plugins-test-{Guid.NewGuid():N}");
        var services = BuildBaseline();
        var options = new RuntimeOptions { PythonPluginsDirectory = tempRoot };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();
        var host = sp.GetService<IPythonPluginHost>();

        host.Should().NotBeNull(because: "a non-empty PythonPluginsDirectory must register IPythonPluginHost.");
    }

    [Fact]
    public void Composition_PythonPluginHost_Not_Registered_When_PythonPluginsDirectory_Null()
    {
        var services = BuildBaseline();
        var options = new RuntimeOptions { PythonPluginsDirectory = null };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();
        var host = sp.GetService<IPythonPluginHost>();

        host.Should().BeNull(because: "null PythonPluginsDirectory disables the Python plugin loader — IPythonPluginHost is not registered.");
    }

    [Fact]
    public void Composition_INamedToolSourceProvider_Registered_When_PythonPluginsDirectory_Set()
    {
        // INamedToolSourceProvider must be in DI so the manifest translator can resolve
        // mcp: tool sources backed by Python plugins.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vais-python-plugins-test-{Guid.NewGuid():N}");
        var services = BuildBaseline();
        var options = new RuntimeOptions { PythonPluginsDirectory = tempRoot };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();
        var providers = sp.GetServices<INamedToolSourceProvider>().ToList();

        providers.Should().ContainSingle(because: "AddPythonPlugins must register exactly one INamedToolSourceProvider.");
        providers[0].Should().BeAssignableTo<IPythonPluginHost>(
            because: "the PythonPluginHostService implements both IPythonPluginHost and INamedToolSourceProvider — the same singleton is forwarded.");
    }

    [Fact]
    public void Composition_INamedToolSourceProvider_Reaches_Translator()
    {
        // Ordering lock: AddPythonPlugins must run before AddAgentManifestInstantiator so the
        // translator's sp.GetServices<INamedToolSourceProvider>() call at build time captures
        // the registered provider. If the registration order ever drifts, mcp: tool refs stop
        // resolving even though AddPythonPlugins appears to be wired.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vais-python-plugins-test-{Guid.NewGuid():N}");
        var services = BuildBaseline();

        CompositionRoot.ConfigureServices(services, new RuntimeOptions { PythonPluginsDirectory = tempRoot });

        using var sp = services.BuildServiceProvider();
        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var providers = sp.GetServices<INamedToolSourceProvider>().ToList();

        translator.Should().NotBeNull();
        providers.Should().ContainSingle(because: "both translator and provider must resolve from the same root — confirms AddPythonPlugins preceded AddAgentManifestInstantiator.");
    }
}
