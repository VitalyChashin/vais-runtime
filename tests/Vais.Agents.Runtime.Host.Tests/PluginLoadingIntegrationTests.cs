// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
/// v0.18 Pillar C PR 3 — end-to-end integration test. Copies the sibling
/// <c>Vais.Agents.Runtime.Host.PluginFixture</c> build output into a temp
/// plugins directory, boots the runtime composition root against it, and
/// verifies the loader + translator + grain-activation chain materialises the
/// plugin's <see cref="IAiAgent"/> into <see cref="StatefulAgentOptions.Agent"/>.
/// </summary>
public class PluginLoadingIntegrationTests : IDisposable
{
    private const string PluginAssemblyName = "Vais.Agents.Runtime.Host.PluginFixture";
    private const string PluginHandlerTypeName = "Vais.Agents.Runtime.Host.PluginFixture.WeatherAgent";

    private readonly string _tempRoot;

    public PluginLoadingIntegrationTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("vais-plugin-it-").FullName;
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
            // Plugins load into non-collectible AssemblyLoadContexts, so the DLLs
            // remain file-locked for the process lifetime. Best-effort cleanup;
            // xunit's temp runs won't leak across CI jobs.
        }
    }

    [Fact]
    public async Task Runtime_Loads_Plugin_And_Translator_Returns_Plugin_Agent()
    {
        var pluginDir = StagePluginFixture();

        var services = BuildHostServices(pluginDir);
        using var sp = services.BuildServiceProvider();

        // 1. Plugin registry populated.
        var registry = sp.GetRequiredService<IPluginHandlerRegistry>();
        registry.HandlerTypeNames.Should().Contain(PluginHandlerTypeName,
            because: "[assembly: VaisPlugin] declared this handler on the fixture assembly.");

        // 2. Seed a manifest that routes to the plugin (null ModelSpec; plugin owns execution).
        //    The composition root wires OrleansAgentRegistry which needs a real silo for RPC;
        //    integration tests stub it with an in-memory registry that shares the IAgentRegistry
        //    contract. The translator queries IAgentRegistry.GetAsync — both impls satisfy it.
        var memoryRegistry = sp.GetRequiredService<InMemoryAgentRegistry>();
        memoryRegistry.Register(new AgentManifest(
            Id: "weather",
            Version: "1.0",
            Handler: new AgentHandlerRef(PluginHandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>()));

        // 3. Translator activates the manifest → options.Agent populated from plugin.
        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var options = await translator.TranslateAsync("weather");

        options.Agent.Should().NotBeNull(because: "the plugin branch of the translator must populate options.Agent.");
        options.Agent!.GetType().FullName.Should().Be(PluginHandlerTypeName);

        // 4. Calling the agent end-to-end returns the plugin's hardcoded reply.
        var reply = await options.Agent.AskAsync("hello");
        reply.Should().Be("Sunny!", because: "WeatherAgent.AskAsync is a trivial hardcoded responder.");
    }

    [Fact]
    public async Task Runtime_With_Empty_PluginsDirectory_Skips_Loader()
    {
        // Explicit empty string disables the loader — no registry registered.
        var services = BuildHostServices(pluginsDirectory: "");
        using var sp = services.BuildServiceProvider();

        sp.GetService<IPluginHandlerRegistry>().Should().BeNull();

        // Without a plugin match AND without a Model, the translator surfaces handler-not-loaded.
        var memoryRegistry = sp.GetRequiredService<InMemoryAgentRegistry>();
        memoryRegistry.Register(new AgentManifest(
            Id: "weather",
            Version: "1.0",
            Handler: new AgentHandlerRef(PluginHandlerTypeName),
            Protocols: Array.Empty<ProtocolBinding>(),
            Tools: Array.Empty<ToolRef>()));

        var translator = sp.GetRequiredService<IAgentManifestTranslator>();
        var act = async () => await translator.TranslateAsync("weather");

        var ex = await act.Should().ThrowAsync<ManifestInstantiationException>();
        ex.Which.Urn.Should().Be(ManifestInstantiationUrns.HandlerNotLoaded);
    }

    private string StagePluginFixture()
    {
        // ProjectReference with ReferenceOutputAssembly=false builds the fixture but doesn't
        // copy it into this test assembly's bin. Resolve the sibling bin by walking up from
        // AppContext.BaseDirectory (.../tests/Vais.Agents.Runtime.Host.Tests/bin/<Config>/<TFM>)
        // to the solution's tests/ root, then down into the fixture's matching Config + TFM.
        var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfmName = Path.GetFileName(testBin);                                         // net9.0
        var configDir = Path.GetDirectoryName(testBin)!;                                 // .../Tests/bin/Debug
        var configName = Path.GetFileName(configDir);                                    // Debug
        var binDir = Path.GetDirectoryName(configDir)!;                                  // .../Tests/bin
        var testsProjectDir = Path.GetDirectoryName(binDir)!;                            // .../Tests
        var testsRootDir = Path.GetDirectoryName(testsProjectDir)!;                      // .../tests
        var fixtureBin = Path.Combine(testsRootDir, PluginAssemblyName, "bin", configName, tfmName);

        // Sanity: caller hasn't torn down the fixture bin.
        Directory.Exists(fixtureBin).Should().BeTrue(because: $"fixture bin must exist at {fixtureBin} — ensure 'dotnet build' ran before the test.");
        File.Exists(Path.Combine(fixtureBin, PluginAssemblyName + ".dll")).Should().BeTrue();

        // Loader convention: one plugin per subfolder named <AssemblyName>. Copy the fixture's
        // entire publish-like bin into <temp>/<AssemblyName>/ so the loader picks the DLL up.
        var pluginSubdir = Path.Combine(_tempRoot, PluginAssemblyName);
        Directory.CreateDirectory(pluginSubdir);
        foreach (var src in Directory.EnumerateFiles(fixtureBin, "*", SearchOption.TopDirectoryOnly))
        {
            var dst = Path.Combine(pluginSubdir, Path.GetFileName(src));
            File.Copy(src, dst, overwrite: true);
        }

        return _tempRoot;
    }

    private static ServiceCollection BuildHostServices(string pluginsDirectory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IGrainFactory>());
        services.AddSingleton(Substitute.For<IClusterClient>());
        services.AddLogging(lb => lb.SetMinimumLevel(LogLevel.Warning));

        CompositionRoot.ConfigureServices(services, new RuntimeOptions { PluginsDirectory = pluginsDirectory });

        // Swap the Orleans-backed registry for an in-memory one — the composition root wires
        // OrleansAgentRegistry, but without a real silo for grain RPC it can't round-trip a
        // manifest. The translator only depends on IAgentRegistry; both impls satisfy it.
        var registry = new InMemoryAgentRegistry();
        services.RemoveAll<IAgentRegistry>();
        services.AddSingleton(registry);
        services.AddSingleton<IAgentRegistry>(registry);
        return services;
    }
}
