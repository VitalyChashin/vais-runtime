// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Tests;

/// <summary>
/// Directory-scan and error-path tests that don't require a compiled fixture
/// plugin. End-to-end "load + resolve + activate against a real DLL" is
/// covered by the <c>PluginLoadingIntegrationTests</c> that ship in PR 3 once
/// the sample plugin project exists.
/// </summary>
public class AssemblyPluginLoaderTests
{
    [Fact]
    public void Load_Missing_Directory_Is_NoOp()
    {
        var loader = new AssemblyPluginLoader();
        var registry = new PluginHandlerRegistry();

        var act = () => loader.Load(Path.Combine(Path.GetTempPath(), $"vais-missing-{Guid.NewGuid():N}"), registry);

        act.Should().NotThrow();
        registry.HandlerTypeNames.Should().BeEmpty();
        registry.Plugins.Should().BeEmpty();
    }

    [Fact]
    public void Load_Empty_Directory_Is_NoOp()
    {
        var dir = Directory.CreateTempSubdirectory("vais-plugins-empty-");
        try
        {
            var loader = new AssemblyPluginLoader();
            var registry = new PluginHandlerRegistry();

            loader.Load(dir.FullName, registry);

            registry.HandlerTypeNames.Should().BeEmpty();
            registry.Plugins.Should().BeEmpty();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_Subfolder_With_Garbage_File_Logs_Warning_But_Does_Not_Throw()
    {
        var dir = Directory.CreateTempSubdirectory("vais-plugins-garbage-");
        try
        {
            var pluginDir = dir.CreateSubdirectory("bad-plugin");
            File.WriteAllText(Path.Combine(pluginDir.FullName, "bad-plugin.dll"), "this is not a valid PE file");

            var loader = new AssemblyPluginLoader();
            var registry = new PluginHandlerRegistry();

            var act = () => loader.Load(dir.FullName, registry);

            act.Should().NotThrow(because: "individual plugin load failures log warnings and continue; only a directory-root problem or handler collision throws.");
            registry.HandlerTypeNames.Should().BeEmpty();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_Subfolder_With_No_Dll_Is_Skipped()
    {
        var dir = Directory.CreateTempSubdirectory("vais-plugins-nodll-");
        try
        {
            var pluginDir = dir.CreateSubdirectory("bare-plugin");
            File.WriteAllText(Path.Combine(pluginDir.FullName, "readme.md"), "no DLL here");

            var loader = new AssemblyPluginLoader();
            var registry = new PluginHandlerRegistry();

            loader.Load(dir.FullName, registry);

            registry.HandlerTypeNames.Should().BeEmpty();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
