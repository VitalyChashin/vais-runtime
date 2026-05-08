// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Runtime.Plugins;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Unit tests for <see cref="DefaultContainerPluginReloader"/>.
/// </summary>
public sealed class DefaultContainerPluginReloaderTests
{
    private static ContainerPluginHostService MakeEmptyHost()
    {
        var options = new ContainerPluginLoaderOptions
        {
            PluginsDirectory = Path.GetTempPath(),
        };
        var registry = Substitute.For<IPluginHandlerRegistry>();
        return new ContainerPluginHostService(options, registry, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task ReloadAsync_NoSupervisorLoaded_ReturnsNoSupervisorStatus()
    {
        var host = MakeEmptyHost();
        var reloader = new DefaultContainerPluginReloader(host);

        var result = await reloader.ReloadAsync("nonexistent-plugin", "my-registry/my-plugin:1.0", CancellationToken.None);

        result.Status.Should().Be(ContainerPluginReloadStatus.NoSupervisor);
        result.PluginName.Should().Be("nonexistent-plugin");
        result.FailureUrn.Should().Be(ContainerPluginUrns.NoSupervisor);
    }

    [Fact]
    public async Task ReloadAsync_NoSupervisorLoaded_NullFailureException()
    {
        var host = MakeEmptyHost();
        var reloader = new DefaultContainerPluginReloader(host);

        var result = await reloader.ReloadAsync("any-plugin", "image:tag", CancellationToken.None);

        result.FailureException.Should().BeNull();
    }

    [Fact]
    public async Task ReloadAsync_EmptyPluginName_StillReturnsNoSupervisor()
    {
        var host = MakeEmptyHost();
        var reloader = new DefaultContainerPluginReloader(host);

        var result = await reloader.ReloadAsync("", "image:tag", CancellationToken.None);

        result.Status.Should().Be(ContainerPluginReloadStatus.NoSupervisor);
    }
}
