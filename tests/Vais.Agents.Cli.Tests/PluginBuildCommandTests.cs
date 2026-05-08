// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class PluginBuildCommandTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly Func<string, CancellationToken, Task<int>> _savedDockerRun = PluginBuildCommand.DockerRun;

    public PluginBuildCommandTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        PluginBuildCommand.DockerRun = _savedDockerRun;
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ReadImageFromPluginYaml_ReturnsImageValue()
    {
        File.WriteAllText(Path.Combine(_tempDir, "plugin.yaml"),
            "apiVersion: vais.agents/v1\nspec:\n  runtime: container\n  image: my-registry/plugin:1.0\n");

        var image = PluginBuildCommand.ReadImageFromPluginYaml(_tempDir);

        image.Should().Be("my-registry/plugin:1.0");
    }

    [Fact]
    public void ReadImageFromPluginYaml_NoFile_ReturnsNull()
    {
        var image = PluginBuildCommand.ReadImageFromPluginYaml(_tempDir);

        image.Should().BeNull();
    }

    [Fact]
    public void ReadImageFromPluginYaml_NoImageField_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_tempDir, "plugin.yaml"),
            "apiVersion: vais.agents/v1\nspec:\n  runtime: python\n  entrypoint: src/server.py\n");

        var image = PluginBuildCommand.ReadImageFromPluginYaml(_tempDir);

        image.Should().BeNull();
    }

    [Fact]
    public void DockerRun_DefaultDelegate_IsNotNull()
    {
        PluginBuildCommand.DockerRun.Should().NotBeNull();
    }

    [Fact]
    public void DockerRun_CanBeReplaced_ForTestInjection()
    {
        var called = false;
        PluginBuildCommand.DockerRun = (_, _) => { called = true; return Task.FromResult(0); };

        PluginBuildCommand.DockerRun("build -t test:1.0 .", CancellationToken.None);

        called.Should().BeTrue();
    }
}
