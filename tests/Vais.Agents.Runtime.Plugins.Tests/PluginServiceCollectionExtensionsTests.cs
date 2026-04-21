// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Tests;

public class PluginServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAgentPlugins_Registers_IPluginHandlerRegistry_Singleton()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddAgentPlugins(Path.Combine(Path.GetTempPath(), $"vais-does-not-exist-{Guid.NewGuid():N}"));

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IPluginHandlerRegistry>();

        registry.HandlerTypeNames.Should().BeEmpty(because: "directory is missing, so the loader is a no-op but still registers an empty registry.");
    }

    [Fact]
    public void AddAgentPlugins_Returns_Same_ServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var returned = services.AddAgentPlugins(Path.Combine(Path.GetTempPath(), $"vais-{Guid.NewGuid():N}"));

        returned.Should().BeSameAs(services);
    }

    [Fact]
    public void AddAgentPlugins_Empty_Directory_Throws_Argument()
    {
        var services = new ServiceCollection();

        Action act = () => services.AddAgentPlugins("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddAgentPlugins_Null_Services_Throws()
    {
        IServiceCollection? services = null;

        Action act = () => services!.AddAgentPlugins("/tmp");

        act.Should().Throw<ArgumentNullException>();
    }
}
