// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Control.Policy.Opa;
using Vais.Agents.Core;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Persistence.Postgres;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Plugins;
using Vais.Agents.Runtime.Plugins.Python;
using Xunit;

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

        // IConfiguration is always present in ASP.NET Core; required by HmacCallTokenService
        // (registered when PythonPluginsDirectory is set) to read the call-token secret.
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Vais:ContainerPlugin:CallTokenSecret"] = "test-secret-at-least-32-chars-long-xxxx"
                })
                .Build());

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
            because: "the host installs the durable Orleans store via services.Replace, so it wins over the in-memory control-plane default.");
    }

    [Fact]
    public void Composition_Registers_OrleansBacked_InvokeLeaseStore()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IInvokeLeaseStore>().Should().BeOfType<OrleansInvokeLeaseStore>(
            because: "the runtime registers the grain-backed lease store (P1) ahead of the in-memory fallback, "
                   + "so session-mode call-token liveness is reachable from any silo.");
    }

    [Fact]
    public void Composition_Idempotency_OrleansStore_Wins_Over_PreRegistered_Default()
    {
        // M1 order-independence: even if a competing IIdempotencyStore is already in the
        // container before ConfigureServices runs (the v0.11 footgun shape), services.Replace
        // guarantees the durable Orleans store wins — registration order no longer matters.
        var services = BuildBaseline();
        services.AddSingleton<IIdempotencyStore>(Substitute.For<IIdempotencyStore>());

        CompositionRoot.ConfigureServices(services, new RuntimeOptions());

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IIdempotencyStore>().Should().BeOfType<OrleansIdempotencyStore>(
            because: "services.Replace overrides any prior IIdempotencyStore registration regardless of order.");
    }

    [Fact]
    public void CriticalContracts_Verify_Passes_For_Full_CompositionRoot()
    {
        // M3: the startup self-check must succeed against the fully-wired composition root —
        // every always-on critical contract resolves.
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions());
        using var sp = services.BuildServiceProvider();

        var act = () => CriticalRuntimeContracts.Verify(sp);

        act.Should().NotThrow(
            because: "ConfigureServices registers every contract in CriticalRuntimeContracts.All.");
    }

    [Fact]
    public void CriticalContracts_Verify_Throws_Naming_Missing_Contracts()
    {
        // M3: if a critical contract is unregistered, the boot must fail with a message that
        // names the offender — not defer to a first-invoke ManifestInstantiationException.
        var services = BuildBaseline(); // baseline only — no runtime contracts registered
        using var sp = services.BuildServiceProvider();

        var act = () => CriticalRuntimeContracts.Verify(sp);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*IAgentManifestTranslator*",
                because: "Verify must fail fast and name the missing critical contract(s).");
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
    public void Composition_With_CheckpointerConnection_Uses_PostgresCheckpointer()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions
        {
            CheckpointerConnection = "Host=localhost;Database=t;Username=u;Password=p",
        });

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IGraphCheckpointer>().Should().BeOfType<PostgresGraphCheckpointer>(
            "VAIS_CHECKPOINTER_CONNECTION swaps the checkpointer backend to Postgres");
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
    public void Options_EffectiveStreamingBackend_Resolves_Defaults()
    {
        // localhost + postgres-clustered default to memory streams; redis-clustered defaults to
        // redis streams; an explicit StreamingBackend always wins (G1).
        new RuntimeOptions { Mode = "localhost" }
            .EffectiveStreamingBackend.Should().Be("memory");
        new RuntimeOptions { Mode = "clustered", ClusteringBackend = "redis", RedisConnection = "r:6379" }
            .EffectiveStreamingBackend.Should().Be("redis");
        new RuntimeOptions { Mode = "clustered", ClusteringBackend = "postgres", PostgresConnection = "Host=p" }
            .EffectiveStreamingBackend.Should().Be("memory");
        new RuntimeOptions
        {
            Mode = "clustered", ClusteringBackend = "postgres", PostgresConnection = "Host=p",
            RedisConnection = "r:6379", StreamingBackend = "redis",
        }.EffectiveStreamingBackend.Should().Be("redis");
        new RuntimeOptions
        {
            Mode = "clustered", ClusteringBackend = "redis", RedisConnection = "r:6379",
            StreamingBackend = "memory",
        }.EffectiveStreamingBackend.Should().Be("memory");
    }

    [Fact]
    public void Options_Postgres_Clustering_With_Redis_Streaming_Requires_RedisConnection()
    {
        var options = new RuntimeOptions
        {
            Mode = "clustered",
            ClusteringBackend = "postgres",
            PostgresConnection = "Host=pg;Database=vais",
            StreamingBackend = "redis",
            RedisConnection = null,
        };

        var act = () => options.EnsureValid();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VAIS_REDIS_CONNECTION*required*stream*",
                because: "Redis streams need a Redis connection even when clustering is Postgres.");
    }

    [Fact]
    public void Options_Postgres_Clustering_With_Redis_Streaming_And_Connections_Is_Valid()
    {
        var options = new RuntimeOptions
        {
            Mode = "clustered",
            ClusteringBackend = "postgres",
            PostgresConnection = "Host=pg;Database=vais",
            StreamingBackend = "redis",
            RedisConnection = "redis:6379",
        };

        options.EffectiveStreamingBackend.Should().Be("redis");
        options.Invoking(o => o.EnsureValid()).Should().NotThrow();
    }

    [Fact]
    public void Options_Invalid_StreamingBackend_Throws()
    {
        var options = new RuntimeOptions
        {
            Mode = "clustered",
            ClusteringBackend = "postgres",
            PostgresConnection = "Host=pg;Database=vais",
            StreamingBackend = "kafka",
        };

        var act = () => options.EnsureValid();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VAIS_STREAMING_BACKEND*memory*redis*");
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

    // NOTE: the former Composition_Plugin_Registry_Registered_Before_Translator test was removed.
    // It claimed to lock a registration ordering that is not actually load-bearing (the translator
    // resolves IPluginHandlerRegistry lazily) and structurally could not observe what it asserted.
    // Order-independence is now proven behaviorally by
    // AgentManifestInstantiatorRegistrationTests.Translator_Uses_PluginRegistry_Registered_After_Instantiator;
    // the registry-when-dir-set behavior is covered by Composition_Plugin_Registry_Registered_When_PluginsDirectory_Set.

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

        providers.Should().ContainSingle(
            p => p is IPythonPluginHost,
            because: "AddPythonPlugins must register one INamedToolSourceProvider backed by IPythonPluginHost — the same singleton is forwarded.");
    }

    [Fact]
    public void Composition_JwtAuth_Not_Registered_When_JwtAuthority_Null()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { JwtAuthority = null });

        using var sp = services.BuildServiceProvider();

        sp.GetService<IAuthenticationSchemeProvider>().Should().BeNull(
            because: "no VAIS_JWT_AUTHORITY → AddAuthentication is never called → auth pipeline is absent.");
    }

    [Fact]
    public void Composition_JwtAuth_Registered_When_JwtAuthority_Set()
    {
        var services = BuildBaseline();
        var options = new RuntimeOptions { JwtAuthority = "https://oidc.example.com/realms/test" };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();

        sp.GetService<IAuthenticationSchemeProvider>().Should().NotBeNull(
            because: "VAIS_JWT_AUTHORITY set → AddAgentControlPlaneJwtAuth must wire authentication services.");

        sp.GetRequiredService<IPrincipalMapper>().Should().BeOfType<DefaultPrincipalMapper>(
            because: "without UseSaPrincipalMapper, the default mapper must be registered by AddAgentControlPlaneJwtAuth.");
    }

    [Fact]
    public void Composition_ServiceAccountPrincipalMapper_Registered_When_UseSaPrincipalMapper_Set()
    {
        var services = BuildBaseline();
        var options = new RuntimeOptions
        {
            JwtAuthority = "https://oidc.example.com/realms/test",
            UseSaPrincipalMapper = true,
        };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IPrincipalMapper>().Should().BeOfType<ServiceAccountPrincipalMapper>(
            because: "UseSaPrincipalMapper=true installs ServiceAccountPrincipalMapper via services.Replace, overriding the JWT-auth DefaultPrincipalMapper default.");
    }

    [Fact]
    public void Composition_SaPrincipalMapper_Wins_Over_PreRegistered_Default()
    {
        // M1 order-independence: a competing IPrincipalMapper already in the container before
        // ConfigureServices runs must not defeat the SA mapper — services.Replace overrides it.
        var services = BuildBaseline();
        services.AddSingleton<IPrincipalMapper>(Substitute.For<IPrincipalMapper>());

        var options = new RuntimeOptions
        {
            JwtAuthority = "https://oidc.example.com/realms/test",
            UseSaPrincipalMapper = true,
        };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IPrincipalMapper>().Should().BeOfType<ServiceAccountPrincipalMapper>(
            because: "services.Replace overrides any prior IPrincipalMapper registration regardless of order.");
    }

    [Fact]
    public void Composition_JwtAuth_ContextAccessor_Registered_When_JwtAuthority_Set()
    {
        var services = BuildBaseline();
        var options = new RuntimeOptions { JwtAuthority = "https://oidc.example.com/realms/test" };

        CompositionRoot.ConfigureServices(services, options);

        using var sp = services.BuildServiceProvider();

        sp.GetService<AsyncLocalAgentContextAccessor>().Should().NotBeNull(
            because: "AddAgentControlPlaneJwtAuth must wire AsyncLocalAgentContextAccessor for the principal-mapping middleware to push principals onto.");
    }

    // NOTE: the former Composition_INamedToolSourceProvider_Reaches_Translator test was removed —
    // same reason as the plugin-registry ordering test above (claimed an ordering it could not
    // observe; order is irrelevant). Provider-when-dir-set behavior remains covered by
    // Composition_INamedToolSourceProvider_Registered_When_PythonPluginsDirectory_Set.

    [Fact]
    public void Options_LocalhostPersistence_Postgres_Requires_PostgresConnection()
    {
        var options = new RuntimeOptions
        {
            Mode = "localhost",
            LocalhostPersistence = LocalhostPersistenceMode.Postgres,
            PostgresConnection = null,
        };

        var act = () => options.EnsureValid();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VAIS_POSTGRES_CONNECTION*required*",
                because: "localhost Postgres persistence without a connection string must fail loudly at startup.");
    }

    [Fact]
    public void Options_LocalhostPubSubPersistence_Postgres_Requires_PostgresConnection()
    {
        var options = new RuntimeOptions
        {
            Mode = "localhost",
            LocalhostPubSubPersistence = LocalhostPersistenceMode.Postgres,
            PostgresConnection = null,
        };

        var act = () => options.EnsureValid();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*VAIS_POSTGRES_CONNECTION*required*",
                because: "localhost Postgres pub-sub persistence without a connection string must fail loudly at startup.");
    }

    [Fact]
    public void Options_LocalhostPersistence_Memory_Does_Not_Require_PostgresConnection()
    {
        var options = new RuntimeOptions
        {
            Mode = "localhost",
            LocalhostPersistence = LocalhostPersistenceMode.Memory,
            LocalhostPubSubPersistence = LocalhostPersistenceMode.Memory,
            PostgresConnection = null,
        };

        var act = () => options.EnsureValid();

        act.Should().NotThrow(because: "memory persistence is the default and needs no external deps.");
    }

    [Fact]
    public void Options_FromEnvironment_Reads_LocalhostPersistence()
    {
        Environment.SetEnvironmentVariable("VAIS_LOCALHOST_PERSISTENCE", "postgres");
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.LocalhostPersistence.Should().Be(LocalhostPersistenceMode.Postgres);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_LOCALHOST_PERSISTENCE", null);
        }
    }

    [Fact]
    public void Options_FromEnvironment_LocalhostPersistence_Defaults_To_Memory()
    {
        Environment.SetEnvironmentVariable("VAIS_LOCALHOST_PERSISTENCE", null);
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.LocalhostPersistence.Should().Be(LocalhostPersistenceMode.Memory,
                because: "unset VAIS_LOCALHOST_PERSISTENCE defaults to memory — no behaviour change for existing deployments.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_LOCALHOST_PERSISTENCE", null);
        }
    }

    [Fact]
    public void Options_FromEnvironment_Reads_LocalhostPubSubPersistence()
    {
        Environment.SetEnvironmentVariable("VAIS_LOCALHOST_PUBSUB_PERSISTENCE", "postgres");
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.LocalhostPubSubPersistence.Should().Be(LocalhostPersistenceMode.Postgres);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_LOCALHOST_PUBSUB_PERSISTENCE", null);
        }
    }

    [Fact]
    public void Composition_BootManifestApplyService_Registered_When_Directory_Set()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"vais-boot-manifests-test-{Guid.NewGuid():N}");
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { BootManifestsDirectory = tempRoot });

        using var sp = services.BuildServiceProvider();
        var hostedServices = sp.GetServices<IHostedService>();

        hostedServices.Should().ContainSingle(
            s => s is BootManifestApplyService,
            because: "a non-empty BootManifestsDirectory must register BootManifestApplyService as a hosted service.");
    }

    [Fact]
    public void Composition_BootManifestApplyService_Not_Registered_When_Directory_Null()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { BootManifestsDirectory = null });

        using var sp = services.BuildServiceProvider();
        var hostedServices = sp.GetServices<IHostedService>();

        hostedServices.Should().NotContain(
            s => s is BootManifestApplyService,
            because: "null BootManifestsDirectory disables boot-apply — BootManifestApplyService must not be registered.");
    }

    [Fact]
    public void Options_FromEnvironment_Reads_BootManifestsDirectory()
    {
        Environment.SetEnvironmentVariable("VAIS_BOOT_MANIFESTS_DIRECTORY", "/var/lib/vais/manifests");
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.BootManifestsDirectory.Should().Be("/var/lib/vais/manifests");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_BOOT_MANIFESTS_DIRECTORY", null);
        }
    }

    [Fact]
    public void Options_FromEnvironment_BootManifestsDirectory_Null_When_Unset()
    {
        Environment.SetEnvironmentVariable("VAIS_BOOT_MANIFESTS_DIRECTORY", null);
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.BootManifestsDirectory.Should().BeNull(
                because: "unset VAIS_BOOT_MANIFESTS_DIRECTORY disables boot-apply.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_BOOT_MANIFESTS_DIRECTORY", null);
        }
    }

    // ── RC-8: A2A / PowerFx / Idempotency gating ─────────────────────────────

    [Fact]
    public void ConfigureServices_A2aDisabled_DoesNotRegisterIA2AGraphNodeInvoker()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { A2aEnabled = false });

        using var sp = services.BuildServiceProvider();
        sp.GetService<IA2AGraphNodeInvoker>().Should().BeNull(
            because: "VAIS_A2A_ENABLED=false must skip AddA2AGraphNodeInvoker so graph nodes cannot delegate to remote A2A runtimes.");
    }

    [Fact]
    public void ConfigureServices_A2aEnabled_RegistersIA2AGraphNodeInvoker()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { A2aEnabled = true });

        using var sp = services.BuildServiceProvider();
        sp.GetService<IA2AGraphNodeInvoker>().Should().NotBeNull(
            because: "A2aEnabled=true (default) must register IA2AGraphNodeInvoker.");
    }

    [Fact]
    public void ConfigureServices_PowerFxDisabled_DoesNotRegisterIGraphExpressionEvaluator()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { PowerFxEnabled = false });

        using var sp = services.BuildServiceProvider();
        sp.GetService<IGraphExpressionEvaluator>().Should().BeNull(
            because: "VAIS_POWERFX_ENABLED=false must skip AddPowerFxExpressionEvaluator so graph edge PowerFx predicates are not evaluated.");
    }

    [Fact]
    public void ConfigureServices_PowerFxEnabled_RegistersIGraphExpressionEvaluator()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { PowerFxEnabled = true });

        using var sp = services.BuildServiceProvider();
        sp.GetService<IGraphExpressionEvaluator>().Should().NotBeNull(
            because: "PowerFxEnabled=true (default) must register IGraphExpressionEvaluator.");
    }

    [Fact]
    public void ConfigureServices_IdempotencyDisabled_DoesNotRegisterIIdempotencyStore()
    {
        var services = BuildBaseline();
        CompositionRoot.ConfigureServices(services, new RuntimeOptions { IdempotencyEnabled = false });

        using var sp = services.BuildServiceProvider();
        sp.GetService<IIdempotencyStore>().Should().BeNull(
            because: "VAIS_IDEMPOTENCY_ENABLED=false must skip AddAgentControlPlaneIdempotency so duplicate control-plane requests are not detected.");
    }

    // ── RC-7: RuntimeOptions.FromEnvironment env-var parsing ─────────────────

    [Fact]
    public void RuntimeOptions_FromEnvironment_A2aDefaultTrue()
    {
        Environment.SetEnvironmentVariable("VAIS_A2A_ENABLED", null);
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.A2aEnabled.Should().BeTrue(
                because: "unset VAIS_A2A_ENABLED must default to true — existing deployments are unaffected.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_A2A_ENABLED", null);
        }
    }

    [Fact]
    public void RuntimeOptions_FromEnvironment_A2aFalseWhenEnvVarSetToFalse()
    {
        Environment.SetEnvironmentVariable("VAIS_A2A_ENABLED", "false");
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.A2aEnabled.Should().BeFalse(
                because: "VAIS_A2A_ENABLED=false must disable A2A graph-node invocation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_A2A_ENABLED", null);
        }
    }

    [Fact]
    public void RuntimeOptions_FromEnvironment_PowerFxDefaultTrue()
    {
        Environment.SetEnvironmentVariable("VAIS_POWERFX_ENABLED", null);
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.PowerFxEnabled.Should().BeTrue(
                because: "unset VAIS_POWERFX_ENABLED must default to true.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_POWERFX_ENABLED", null);
        }
    }

    [Fact]
    public void RuntimeOptions_FromEnvironment_IdempotencyDefaultTrue()
    {
        Environment.SetEnvironmentVariable("VAIS_IDEMPOTENCY_ENABLED", null);
        try
        {
            var options = RuntimeOptions.FromEnvironment();
            options.IdempotencyEnabled.Should().BeTrue(
                because: "unset VAIS_IDEMPOTENCY_ENABLED must default to true.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VAIS_IDEMPOTENCY_ENABLED", null);
        }
    }
}
