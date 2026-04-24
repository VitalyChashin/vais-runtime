// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Vais.Agents.Control;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Host;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Plugins;
using Xunit;

namespace Vais.Agents.Runtime.Host.Tests;

/// <summary>
/// v0.22 Pillar F PR 3 — end-to-end hot-reload integration tests. Stages V1 and
/// V2 of the <c>Vais.Agents.Runtime.Host.PluginFixtureHotReload</c> fixture into
/// separate temp directories (to avoid Windows file locks on collectible ALCs),
/// then drives <see cref="DefaultPluginReloader"/> directly to verify the registry
/// swap works without restarting the host.
/// </summary>
public class PluginHotReloadIntegrationTests : IDisposable
{
    // Both fixtures share the same AssemblyName and handler type name — that is the
    // point: the reloader keys the swap on the plugin name (folder name = AssemblyName),
    // so V1 → V2 is an in-place atomic registry swap, not a new registration.
    private const string PluginAssemblyName = "Vais.Agents.Runtime.Host.PluginFixtureHotReload";
    private const string PluginHandlerTypeName = "Vais.Agents.Runtime.Host.PluginFixtureHotReload.WeatherAgent";

    // Separate source fixture directories for V1 and V2.
    private const string V1FixtureProjectName = "Vais.Agents.Runtime.Host.PluginFixtureHotReloadV1";
    private const string V2FixtureProjectName = "Vais.Agents.Runtime.Host.PluginFixtureHotReloadV2";

    private readonly string _tempRoot;

    public PluginHotReloadIntegrationTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("vais-hr-it-").FullName;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Collectible ALCs hold file locks until the ALC object is GC'd. Best-effort
            // cleanup; CI temp directories are cleaned up between runs anyway.
        }
    }

    [Fact]
    public async Task HotReload_SwapsHandler_WithoutRestartingHost()
    {
        // Stage V1 in <temp>/v1/<AssemblyName>/ (loader scans <temp>/v1/).
        var v1Root = StageFixture(V1FixtureProjectName, subdir: "v1");

        var services = BuildHostServices(v1Root, ReloadPolicy.DrainAndSwap);
        using var sp = services.BuildServiceProvider();

        // Warm up the registry (lazy-loaded on first resolve).
        var registry = sp.GetRequiredService<IPluginHandlerRegistry>();
        registry.HandlerTypeNames.Should().Contain(PluginHandlerTypeName);

        // Seed a manifest and verify V1 responds "Sunny!".
        var memRegistry = sp.GetRequiredService<InMemoryAgentRegistry>();
        memRegistry.Register(new AgentManifest(
            Id: "weather",
            Version: "1.0",
            Handler: new AgentHandlerRef(PluginHandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>()));

        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var v1Options = await translator.TranslateAsync("weather");
        var v1Reply = await v1Options.Agent!.AskAsync("ping");
        v1Reply.Should().Be("Sunny!", because: "V1 fixture always returns Sunny!");

        // Stage V2 in a SEPARATE temp subdirectory to avoid file locks on the
        // V1 DLL (collectible ALCs hold file locks until GC finalisation).
        var v2Root = StageFixture(V2FixtureProjectName, subdir: "v2");
        var v2PluginPath = Path.Combine(v2Root, PluginAssemblyName, PluginAssemblyName + ".dll");

        // Trigger the hot-reload swap.
        var reloader = sp.GetRequiredService<IPluginReloader>();
        var result = await reloader.ReloadAsync(v2PluginPath);

        result.Status.Should().Be(PluginReloadStatus.Success, because: "V2 fixture is a valid v0.18 plugin");
        result.NewDescriptor.Should().NotBeNull();
        result.NewDescriptor!.Name.Should().Be(PluginAssemblyName);

        // Registry now points to V2 — TranslatorInvalidationHook fired during ReloadAsync,
        // so the cache is already cleared; TranslateAsync produces a fresh instance.
        var v2Options = await translator.TranslateAsync("weather");
        var v2Reply = await v2Options.Agent!.AskAsync("ping");
        v2Reply.Should().Be("Rainy!", because: "V2 fixture always returns Rainy!");
    }

    [Fact]
    public async Task HotReload_OldAlc_IsGarbageCollected_AfterSwap()
    {
        var v1Root = StageFixture(V1FixtureProjectName, subdir: "v1");
        var services = BuildHostServices(v1Root, ReloadPolicy.DrainAndSwap);
        var sp = services.BuildServiceProvider();

        // Capture a weak reference to the V1 ALC before the swap.
        // The helper must not inline so the JIT doesn't keep the reference alive.
        var weakAlc = GetV1AlcWeakRef(sp);

        // Stage V2 and trigger the hot-reload via a NoInlining async helper so the
        // compiler-generated state machine (and its TaskAwaiter<PluginReloadResult>,
        // which transitively holds OldDescriptor.LoadContext = the V1 ALC) is confined
        // to a fully-popped frame before the GC loop runs.
        var v2Root = StageFixture(V2FixtureProjectName, subdir: "v2");
        var v2PluginPath = Path.Combine(v2Root, PluginAssemblyName, PluginAssemblyName + ".dll");
        await TriggerReloadAsync(sp, v2PluginPath);

        // Dispose the container so the registry (which holds the old descriptor until
        // the swap completes) releases its last managed reference to the V1 ALC.
        await sp.DisposeAsync();

        // The old ALC was unloaded in DefaultPluginReloader after hooks completed.
        // Force full GC collection — may take a few cycles on collectible ALCs.
        for (var i = 0; i < 5; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        weakAlc.IsAlive.Should().BeFalse(
            because: "the old collectible ALC is unloaded after the swap and must be eligible for GC");
    }

    [Fact]
    public void HotReload_WithPolicy_Disabled_DoesNotRegisterReloader()
    {
        // Verify that the CompositionRoot wiring respects RuntimeOptions.PluginsHotReload = Disabled.
        var v1Root = StageFixture(V1FixtureProjectName, subdir: "v1");
        var services = BuildHostServices(v1Root, ReloadPolicy.Disabled);
        using var sp = services.BuildServiceProvider();

        sp.GetService<IPluginReloader>().Should().BeNull(
            because: "CompositionRoot must not register IPluginReloader when PluginsHotReload is Disabled");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // NoInlining keeps TaskAwaiter<PluginReloadResult> (which holds OldDescriptor.LoadContext)
    // in a fully-popped frame so the V1 ALC is reachable only via weakAlc when the GC runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task TriggerReloadAsync(IServiceProvider sp, string pluginPath)
    {
        await sp.GetRequiredService<IPluginReloader>().ReloadAsync(pluginPath);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference GetV1AlcWeakRef(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<IPluginHandlerRegistry>();
        var alc = registry.Plugins.First().LoadContext;
        return new WeakReference(alc);
    }

    private string StageFixture(string fixtureProjectName, string subdir)
    {
        // Walk from the test assembly's bin directory up to tests/, then down into the
        // fixture's matching config + TFM. Same convention as PluginLoadingIntegrationTests.
        var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfmName = Path.GetFileName(testBin);
        var configDir = Path.GetDirectoryName(testBin)!;
        var configName = Path.GetFileName(configDir);
        var binDir = Path.GetDirectoryName(configDir)!;
        var testsProjectDir = Path.GetDirectoryName(binDir)!;
        var testsRootDir = Path.GetDirectoryName(testsProjectDir)!;
        var fixtureBin = Path.Combine(testsRootDir, fixtureProjectName, "bin", configName, tfmName);

        Directory.Exists(fixtureBin).Should().BeTrue(
            because: $"fixture bin must exist at {fixtureBin} — ensure 'dotnet build' ran before the test.");

        // The loader convention: one plugin per subfolder named <AssemblyName>.
        // Use a subdir under _tempRoot so V1 and V2 never share a directory root.
        var pluginsRoot = Path.Combine(_tempRoot, subdir);
        var pluginSubdir = Path.Combine(pluginsRoot, PluginAssemblyName);
        Directory.CreateDirectory(pluginSubdir);

        foreach (var src in Directory.EnumerateFiles(fixtureBin, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(src, Path.Combine(pluginSubdir, Path.GetFileName(src)), overwrite: true);
        }

        return pluginsRoot;
    }

    private static ServiceCollection BuildHostServices(string pluginsDirectory, ReloadPolicy reloadPolicy)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IGrainFactory>());
        services.AddSingleton(Substitute.For<IClusterClient>());
        services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));

        CompositionRoot.ConfigureServices(services, new RuntimeOptions
        {
            PluginsDirectory = pluginsDirectory,
            PluginsHotReload = reloadPolicy,
        });

        // Swap the Orleans-backed registry for an in-memory one — same pattern as
        // PluginLoadingIntegrationTests. Without a real silo, grain RPC isn't available;
        // the translator only depends on IAgentRegistry.
        var memRegistry = new InMemoryAgentRegistry();
        services.RemoveAll<IAgentRegistry>();
        services.AddSingleton(memRegistry);
        services.AddSingleton<IAgentRegistry>(memRegistry);
        return services;
    }
}
