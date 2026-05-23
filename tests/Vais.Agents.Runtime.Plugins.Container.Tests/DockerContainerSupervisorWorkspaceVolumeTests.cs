// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Volume lifecycle for disk-medium workspaces, driven through a mocked <see cref="IDockerClient"/>:
/// create-on-start (ephemeral pre-cleans), and persistent reclaim only on explicit removal.
/// </summary>
public sealed class DockerContainerSupervisorWorkspaceVolumeTests
{
    private const string VolumeName = "vais-plugin-test-plugin-workspace";

    private static (DockerContainerSupervisor Supervisor, IVolumeOperations Volumes) Build(
        ContainerWorkspaceConfig? workspace)
    {
        var volumes = Substitute.For<IVolumeOperations>();
        volumes.CreateAsync(Arg.Any<VolumesCreateParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new VolumeResponse()));
        var docker = Substitute.For<IDockerClient>();
        docker.Volumes.Returns(volumes);

        var descriptor = new ContainerPluginDescriptor
        {
            Name = "test-plugin",
            Image = "test:1.0",
            Port = 8080,
            InvokeBaseUrl = "http://localhost:8080",
            Workspace = workspace,
        };
        return (new DockerContainerSupervisor(descriptor, docker, NullLogger.Instance), volumes);
    }

    [Fact]
    public async Task EnsureWorkspaceVolume_PersistentDisk_CreatesAndDoesNotPreClean()
    {
        var (sup, volumes) = Build(new ContainerWorkspaceConfig("/workspace", 4096, WorkspaceMedium.Disk, true));
        await sup.EnsureWorkspaceVolumeAsync(default);
        await volumes.Received(1).CreateAsync(
            Arg.Is<VolumesCreateParameters>(p => p.Name == VolumeName), Arg.Any<CancellationToken>());
        await volumes.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureWorkspaceVolume_EphemeralDisk_PreCleansThenCreates()
    {
        var (sup, volumes) = Build(new ContainerWorkspaceConfig("/workspace", 4096, WorkspaceMedium.Disk, false));
        await sup.EnsureWorkspaceVolumeAsync(default);
        await volumes.Received(1).RemoveAsync(VolumeName, Arg.Any<bool?>(), Arg.Any<CancellationToken>());
        await volumes.Received(1).CreateAsync(
            Arg.Is<VolumesCreateParameters>(p => p.Name == VolumeName), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureWorkspaceVolume_Memory_DoesNothing()
    {
        var (sup, volumes) = Build(new ContainerWorkspaceConfig("/workspace", 4096, WorkspaceMedium.Memory, false));
        await sup.EnsureWorkspaceVolumeAsync(default);
        await volumes.DidNotReceive().CreateAsync(Arg.Any<VolumesCreateParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureWorkspaceVolume_NoWorkspace_DoesNothing()
    {
        var (sup, volumes) = Build(null);
        await sup.EnsureWorkspaceVolumeAsync(default);
        await volumes.DidNotReceive().CreateAsync(Arg.Any<VolumesCreateParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePersistentWorkspace_PersistentDisk_RemovesVolume()
    {
        var (sup, volumes) = Build(new ContainerWorkspaceConfig("/workspace", 4096, WorkspaceMedium.Disk, true));
        await sup.RemovePersistentWorkspaceAsync();
        await volumes.Received(1).RemoveAsync(VolumeName, Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePersistentWorkspace_EphemeralDisk_DoesNotRemove()
    {
        var (sup, volumes) = Build(new ContainerWorkspaceConfig("/workspace", 4096, WorkspaceMedium.Disk, false));
        await sup.RemovePersistentWorkspaceAsync();
        await volumes.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovePersistentWorkspace_NoWorkspace_DoesNotRemove()
    {
        var (sup, volumes) = Build(null);
        await sup.RemovePersistentWorkspaceAsync();
        await volumes.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }
}
