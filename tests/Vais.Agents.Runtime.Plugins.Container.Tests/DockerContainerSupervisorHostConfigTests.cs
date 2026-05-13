// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

public sealed class DockerContainerSupervisorHostConfigTests
{
    private static ContainerPluginDescriptor MakeDescriptor(
        long? memoryBytes = null, long? nanoCpus = null, long? pidsLimit = null) =>
        new()
        {
            Name = "test-plugin",
            Image = "test:1.0",
            Port = 8080,
            InvokeBaseUrl = "http://localhost:8080",
            MemoryBytes = memoryBytes,
            NanoCpus = nanoCpus,
            PidsLimit = pidsLimit,
        };

    [Fact]
    public void BuildHostConfig_ReadonlyRootfs_IsTrue()
    {
        var cfg = DockerContainerSupervisor.BuildHostConfig(MakeDescriptor());
        cfg.ReadonlyRootfs.Should().BeTrue();
    }

    [Fact]
    public void BuildHostConfig_TmpfsAtSlashTmp_IsConfigured()
    {
        var cfg = DockerContainerSupervisor.BuildHostConfig(MakeDescriptor());
        cfg.Tmpfs.Should().ContainKey("/tmp");
        cfg.Tmpfs["/tmp"].Should().Be("rw,size=64m,mode=1777");
    }

    [Fact]
    public void BuildHostConfig_CapDrop_ContainsAll()
    {
        var cfg = DockerContainerSupervisor.BuildHostConfig(MakeDescriptor());
        cfg.CapDrop.Should().Contain("ALL");
    }

    [Fact]
    public void BuildHostConfig_SecurityOpt_ContainsNoNewPrivileges()
    {
        var cfg = DockerContainerSupervisor.BuildHostConfig(MakeDescriptor());
        cfg.SecurityOpt.Should().Contain("no-new-privileges:true");
    }

    [Fact]
    public void BuildHostConfig_PortBinding_UsesLoopback()
    {
        var cfg = DockerContainerSupervisor.BuildHostConfig(MakeDescriptor());
        var binding = cfg.PortBindings["8080/tcp"].Single();
        binding.HostIP.Should().Be("127.0.0.1");
        binding.HostPort.Should().Be("8080");
    }

    [Fact]
    public void BuildHostConfig_DefaultsApplied_WhenDescriptorResourcesAreNull()
    {
        var cfg = DockerContainerSupervisor.BuildHostConfig(MakeDescriptor());
        cfg.Memory.Should().Be(256L * 1024 * 1024);
        cfg.MemorySwap.Should().Be(256L * 1024 * 1024);
        cfg.NanoCPUs.Should().Be(500_000_000L);
        cfg.PidsLimit.Should().Be(128);
    }

    [Fact]
    public void BuildHostConfig_DescriptorResources_OverrideDefaults()
    {
        var cfg = DockerContainerSupervisor.BuildHostConfig(
            MakeDescriptor(memoryBytes: 512L * 1024 * 1024, nanoCpus: 1_000_000_000L, pidsLimit: 256));
        cfg.Memory.Should().Be(512L * 1024 * 1024);
        cfg.MemorySwap.Should().Be(512L * 1024 * 1024);
        cfg.NanoCPUs.Should().Be(1_000_000_000L);
        cfg.PidsLimit.Should().Be(256);
    }

    [Fact]
    public void BuildHostConfig_MemorySwap_EqualsMemoory()
    {
        var cfg = DockerContainerSupervisor.BuildHostConfig(MakeDescriptor(memoryBytes: 384L * 1024 * 1024));
        cfg.MemorySwap.Should().Be(cfg.Memory);
    }
}
