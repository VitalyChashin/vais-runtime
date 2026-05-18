// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;
using Vais.Agents.Runtime.Plugins;
using Xunit;

namespace Vais.Agents.Runtime.Host.Tests;

/// <summary>
/// Phase 1 (CHR-7) integration tests for <see cref="IAssemblyDllPusher.PushAsync"/>.
/// Covers: bootstrap, hot-swap, ABI mismatch, validation failure, reload-disabled,
/// idempotent double-push, and ALC unload after swap.
/// </summary>
public class CsharpPluginDllPushIntegrationTests : IDisposable
{
    private const string PluginAssemblyName = "Vais.Agents.Runtime.Host.PluginFixtureHotReload";
    private const string PluginHandlerTypeName = "Vais.Agents.Runtime.Host.PluginFixtureHotReload.WeatherAgent";

    private const string V1FixtureProjectName = "Vais.Agents.Runtime.Host.PluginFixtureHotReloadV1";
    private const string V2FixtureProjectName = "Vais.Agents.Runtime.Host.PluginFixtureHotReloadV2";

    private readonly string _tempRoot;

    public CsharpPluginDllPushIntegrationTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("vais-dll-push-it-").FullName;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Collectible ALCs hold file locks until GC'd. Best-effort cleanup.
        }
    }

    // ── 1. Bootstrap — first-time push returns Bootstrapped ─────────────────

    [Fact]
    public async Task PushAsync_FirstPush_ReturnsBootstrapped()
    {
        var pluginsRoot = Path.Combine(_tempRoot, "t1");
        Directory.CreateDirectory(pluginsRoot);

        using var sp = BuildMinimalDllPushServices(pluginsRoot).BuildServiceProvider();
        var pusher = sp.GetRequiredService<IAssemblyDllPusher>();
        var v1Dll = GetFixtureDllPath(V1FixtureProjectName);

        await using var stream = File.OpenRead(v1Dll);
        var result = await pusher.PushAsync(PluginAssemblyName, stream, "application/octet-stream");

        result.Status.Should().Be(AssemblyDllPushStatus.Bootstrapped,
            because: "the plugin was not in the registry before the push");
        result.Handlers.Should().Contain(PluginHandlerTypeName);
        result.TargetApiVersion.Should().Be("0.18");
        result.ErrorMessage.Should().BeNull();
    }

    // ── 2. Hot-swap — V1 → V2 produces Success and handler behavior changes ─

    [Fact]
    public async Task PushAsync_SecondPush_HotSwapsHandler_And_ReturnsSuccess()
    {
        var v1Root = StageFixture(V1FixtureProjectName, subdir: "t2-v1");
        var services = BuildFullHostServices(v1Root, ReloadPolicy.DrainAndSwap);
        using var sp = services.BuildServiceProvider();

        // Warm up registry with V1.
        sp.GetRequiredService<IPluginHandlerRegistry>().HandlerTypeNames.Should().Contain(PluginHandlerTypeName);

        // Seed a manifest so the translator can resolve the handler.
        var memRegistry = sp.GetRequiredService<InMemoryAgentRegistry>();
        memRegistry.Register(new AgentManifest(
            Id: "weather",
            Version: "1.0",
            Handler: new AgentHandlerRef(PluginHandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>()));

        // V1 agent responds "Sunny!".
        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var v1Opts = await translator.TranslateAsync("weather");
        (await v1Opts.Agent!.AskAsync("ping")).Should().Be("Sunny!");

        // Push V2 DLL (from separate staging to avoid Windows file lock on V1).
        var v2DllPath = GetFixtureDllPath(V2FixtureProjectName);
        var pusher = sp.GetRequiredService<IAssemblyDllPusher>();
        await using var stream = File.OpenRead(v2DllPath);
        var result = await pusher.PushAsync(PluginAssemblyName, stream, "application/octet-stream");

        result.Status.Should().Be(AssemblyDllPushStatus.Success,
            because: "V2 is a valid 0.18 plugin replacing V1");
        result.Handlers.Should().Contain(PluginHandlerTypeName,
            because: "V2 reload result must report the handler type");

        // Registry must now have the V2 factory.
        var registry = sp.GetRequiredService<IPluginHandlerRegistry>();
        registry.HandlerTypeNames.Should().Contain(PluginHandlerTypeName,
            because: "handler must be in registry after reload");

        // V2 agent responds "Rainy!" — handler actually swapped.
        var v2Opts = await translator.TranslateAsync("weather");
        v2Opts.Agent.Should().NotBeNull();
        (await v2Opts.Agent!.AskAsync("ping")).Should().Be("Rainy!");
    }

    // ── 3. ABI mismatch — runtime expects a version the DLL doesn't target ──

    [Fact]
    public async Task PushAsync_RuntimeAbiForcedMismatch_ReturnsAbiMismatch()
    {
        var pluginsRoot = Path.Combine(_tempRoot, "t3");
        Directory.CreateDirectory(pluginsRoot);

        // Wire AddAgentPlugins directly with an overridden runtime ABI of "99.0".
        // The V1 fixture DLL declares ABI "0.18", so pre-validation will reject it.
        using var sp = BuildDllPushServicesWithAbi(pluginsRoot, runtimeAbi: "99.0").BuildServiceProvider();
        var pusher = sp.GetRequiredService<IAssemblyDllPusher>();
        var v1Dll = GetFixtureDllPath(V1FixtureProjectName);

        await using var stream = File.OpenRead(v1Dll);
        var result = await pusher.PushAsync(PluginAssemblyName, stream, "application/octet-stream");

        result.Status.Should().Be(AssemblyDllPushStatus.AbiMismatch,
            because: "V1 fixture targets ABI 0.18 but runtime is configured for 99.0");
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ── 4. Missing [VaisPlugin] attribute → ValidationFailed ─────────────────

    [Fact]
    public async Task PushAsync_DllWithoutVaisPluginAttribute_ReturnsValidationFailed()
    {
        var pluginsRoot = Path.Combine(_tempRoot, "t4");
        Directory.CreateDirectory(pluginsRoot);

        using var sp = BuildMinimalDllPushServices(pluginsRoot).BuildServiceProvider();
        var pusher = sp.GetRequiredService<IAssemblyDllPusher>();

        // Push the test assembly itself — valid .NET PE but has no [VaisPlugin].
        var plainDll = typeof(CsharpPluginDllPushIntegrationTests).Assembly.Location;
        await using var stream = File.OpenRead(plainDll);
        var result = await pusher.PushAsync("TestPlugin", stream, "application/octet-stream");

        result.Status.Should().Be(AssemblyDllPushStatus.ValidationFailed,
            because: "the test assembly has no [VaisPlugin] attribute");
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ── 5. Reload disabled — IAssemblyDllPusher not registered ───────────────

    [Fact]
    public void PushAsync_ReloadDisabled_PusherNotRegistered()
    {
        var pluginsRoot = Path.Combine(_tempRoot, "t5");
        Directory.CreateDirectory(pluginsRoot);

        using var sp = BuildFullHostServices(pluginsRoot, ReloadPolicy.Disabled).BuildServiceProvider();

        sp.GetService<IAssemblyDllPusher>().Should().BeNull(
            because: "IAssemblyDllPusher must not be registered when ReloadPolicy is Disabled");
    }

    // ── 6. Idempotent double-push — second push succeeds without exception ───

    [Fact]
    public async Task PushAsync_SameDllPushedTwice_SecondPushSucceeds()
    {
        var pluginsRoot = Path.Combine(_tempRoot, "t6");
        Directory.CreateDirectory(pluginsRoot);

        using var sp = BuildMinimalDllPushServices(pluginsRoot).BuildServiceProvider();
        var pusher = sp.GetRequiredService<IAssemblyDllPusher>();
        var v1Dll = GetFixtureDllPath(V1FixtureProjectName);

        // First push: bootstrap.
        await using (var s1 = File.OpenRead(v1Dll))
        {
            var r1 = await pusher.PushAsync(PluginAssemblyName, s1, "application/octet-stream");
            r1.Status.Should().Be(AssemblyDllPushStatus.Bootstrapped);
        }

        // Second push: same DLL → Success (hot-swap of existing plugin).
        await using var s2 = File.OpenRead(v1Dll);
        var r2 = await pusher.PushAsync(PluginAssemblyName, s2, "application/octet-stream");
        r2.Status.Should().BeOneOf(
            new[] { AssemblyDllPushStatus.Success, AssemblyDllPushStatus.Bootstrapped },
            because: "pushing the same DLL again must not throw; result is Success or Bootstrapped");
    }

    // ── 7. ALC unload sanity — old ALC is GC'd after swap ───────────────────

    [Fact]
    public async Task PushAsync_AfterSwap_OldAlcIsGarbageCollected()
    {
        var v1Root = StageFixture(V1FixtureProjectName, subdir: "t7-v1");
        var services = BuildFullHostServices(v1Root, ReloadPolicy.DrainAndSwap);
        var sp = services.BuildServiceProvider();

        // Warm up V1 and capture a weak reference to its ALC.
        var weakAlc = GetV1AlcWeakRef(sp);

        // Push V2 from a separate staging path and dispose the service provider
        // so the registry releases its last reference to OldDescriptor.LoadContext.
        var v2DllPath = GetFixtureDllPath(V2FixtureProjectName);
        await PushDllAsync(sp, PluginAssemblyName, v2DllPath);
        await sp.DisposeAsync();

        // Force multiple GC cycles — collectible ALCs may require 2–3 rounds.
        for (var i = 0; i < 5; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        weakAlc.IsAlive.Should().BeFalse(
            because: "the old collectible ALC must be eligible for GC after the swap and disposal");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task PushDllAsync(IServiceProvider sp, string pluginName, string dllPath)
    {
        var pusher = sp.GetRequiredService<IAssemblyDllPusher>();
        await using var stream = File.OpenRead(dllPath);
        await pusher.PushAsync(pluginName, stream, "application/octet-stream");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference GetV1AlcWeakRef(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<IPluginHandlerRegistry>();
        var alc = registry.Plugins.First().LoadContext;
        return new WeakReference(alc);
    }

    private static string GetFixtureDllPath(string fixtureProjectName)
    {
        var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfmName = Path.GetFileName(testBin);
        var configDir = Path.GetDirectoryName(testBin)!;
        var configName = Path.GetFileName(configDir);
        var binDir = Path.GetDirectoryName(configDir)!;
        var testsProjectDir = Path.GetDirectoryName(binDir)!;
        var testsRootDir = Path.GetDirectoryName(testsProjectDir)!;
        var fixtureBin = Path.Combine(testsRootDir, fixtureProjectName, "bin", configName, tfmName);
        return Path.Combine(fixtureBin, PluginAssemblyName + ".dll");
    }

    private string StageFixture(string fixtureProjectName, string subdir)
    {
        var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfmName = Path.GetFileName(testBin);
        var configDir = Path.GetDirectoryName(testBin)!;
        var configName = Path.GetFileName(configDir);
        var binDir = Path.GetDirectoryName(configDir)!;
        var testsProjectDir = Path.GetDirectoryName(binDir)!;
        var testsRootDir = Path.GetDirectoryName(testsProjectDir)!;
        var fixtureBin = Path.Combine(testsRootDir, fixtureProjectName, "bin", configName, tfmName);

        Directory.Exists(fixtureBin).Should().BeTrue(
            because: $"fixture bin must exist at {fixtureBin}");

        var pluginsRoot = Path.Combine(_tempRoot, subdir);
        var pluginSubdir = Path.Combine(pluginsRoot, PluginAssemblyName);
        Directory.CreateDirectory(pluginSubdir);

        foreach (var src in Directory.EnumerateFiles(fixtureBin, "*", SearchOption.TopDirectoryOnly))
            File.Copy(src, Path.Combine(pluginSubdir, Path.GetFileName(src)), overwrite: true);

        return pluginsRoot;
    }

    // Minimal services for tests that only need IAssemblyDllPusher (no translator/agent).
    private static ServiceCollection BuildMinimalDllPushServices(string pluginsDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));
        services.AddAgentPlugins(pluginsDirectory, new PluginLoaderOptions
        {
            ReloadPolicy = ReloadPolicy.DrainAndSwap,
        });
        return services;
    }

    // Minimal services with a custom runtime ABI version (for ABI mismatch test).
    private static ServiceCollection BuildDllPushServicesWithAbi(string pluginsDirectory, string runtimeAbi)
    {
        var services = new ServiceCollection();
        services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));
        services.AddAgentPlugins(pluginsDirectory, new PluginLoaderOptions
        {
            ReloadPolicy = ReloadPolicy.DrainAndSwap,
            RuntimeAbiVersion = runtimeAbi,
        });
        return services;
    }

    // Full host services (CompositionRoot) for tests that need agent translation.
    private static ServiceCollection BuildFullHostServices(string pluginsDirectory, ReloadPolicy reloadPolicy)
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

        var memRegistry = new InMemoryAgentRegistry();
        services.RemoveAll<IAgentRegistry>();
        services.AddSingleton(memRegistry);
        services.AddSingleton<IAgentRegistry>(memRegistry);
        return services;
    }
}
