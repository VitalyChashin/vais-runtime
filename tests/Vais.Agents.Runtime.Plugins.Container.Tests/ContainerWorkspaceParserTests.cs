// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

public sealed class ContainerWorkspaceParserTests
{
    private static readonly ContainerPluginResourceBounds Bounds = new();

    [Fact]
    public void FromSpec_Null_ReturnsNull() =>
        ContainerWorkspaceParser.FromSpec(null, Bounds).Should().BeNull();

    [Fact]
    public void Parse_Defaults_DiskNonPersistent()
    {
        var cfg = ContainerWorkspaceParser.Parse("/workspace", 4096, "disk", false, Bounds);
        cfg.Path.Should().Be("/workspace");
        cfg.SizeMb.Should().Be(4096);
        cfg.Medium.Should().Be(WorkspaceMedium.Disk);
        cfg.Persist.Should().BeFalse();
    }

    [Fact]
    public void Parse_TrailingSlash_IsNormalized() =>
        ContainerWorkspaceParser.Parse("/work/", 10, "disk", false, Bounds).Path.Should().Be("/work");

    [Fact]
    public void Parse_MediumIsCaseInsensitive() =>
        ContainerWorkspaceParser.Parse("/w", 10, "MEMORY", false, Bounds).Medium.Should().Be(WorkspaceMedium.Memory);

    [Fact]
    public void Parse_ClampsSizeToMax()
    {
        var bounds = new ContainerPluginResourceBounds { MaxWorkspaceSizeMb = 1000 };
        ContainerWorkspaceParser.Parse("/w", 5000, "disk", false, bounds).SizeMb.Should().Be(1000);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/tmp")]
    [InlineData("/tmp/")]
    [InlineData("relative")]
    [InlineData("")]
    public void Parse_BadPath_Throws(string path)
    {
        var act = () => ContainerWorkspaceParser.Parse(path, 10, "disk", false, Bounds);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Parse_NonPositiveSize_Throws(int sizeMb)
    {
        var act = () => ContainerWorkspaceParser.Parse("/w", sizeMb, "disk", false, Bounds);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_UnknownMedium_Throws()
    {
        var act = () => ContainerWorkspaceParser.Parse("/w", 10, "s3-snapshot", false, Bounds);
        act.Should().Throw<ArgumentException>().WithMessage("*supported: disk, memory*");
    }

    [Fact]
    public void Parse_PersistWithMemory_Throws()
    {
        var act = () => ContainerWorkspaceParser.Parse("/w", 10, "memory", true, Bounds);
        act.Should().Throw<ArgumentException>().WithMessage("*tmpfs cannot persist*");
    }
}
