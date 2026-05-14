// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

public sealed class PythonPluginBootstrapperTests : IDisposable
{
    private readonly string _pluginDir;

    public PythonPluginBootstrapperTests()
    {
        _pluginDir = Path.Combine(Path.GetTempPath(),
            "vais-bootstrapper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginDir);
    }

    public void Dispose() => Directory.Delete(_pluginDir, recursive: true);

    [Fact]
    public async Task BootstrapAsync_VenvAlreadyExists_SkipsAndReturnsSuccess()
    {
        var venvBin = Directory.CreateDirectory(Path.Combine(_pluginDir, ".venv", "bin")).FullName;
        File.WriteAllText(Path.Combine(venvBin, "python"), "");

        var calls = new List<string>();
        var bootstrapper = new PythonPluginBootstrapper(runner: (psi, _) =>
        {
            calls.Add(psi.FileName);
            return Task.FromResult((0, ""));
        });

        var result = await bootstrapper.BootstrapAsync(_pluginDir, timeoutSeconds: 10);

        result.Success.Should().BeTrue();
        result.ErrorOutput.Should().BeNull();
        calls.Should().BeEmpty("runner must not be called when .venv already exists");
    }

    [Fact]
    public async Task BootstrapAsync_HappyPath_RunsVenvAndPip()
    {
        var commands = new List<string>();
        var bootstrapper = new PythonPluginBootstrapper(runner: (psi, _) =>
        {
            commands.Add(psi.FileName + " " + string.Join(" ", psi.ArgumentList));
            return Task.FromResult((0, ""));
        });

        var result = await bootstrapper.BootstrapAsync(_pluginDir, timeoutSeconds: 10);

        result.Success.Should().BeTrue();
        commands.Should().HaveCount(2);
        commands[0].Should().Contain("python3.11");
        commands[0].Should().Contain("-m venv .venv");
        commands[0].Should().Contain("--system-site-packages");
        commands[1].Should().Contain("pip");
        commands[1].Should().Contain("install");
    }

    [Fact]
    public async Task BootstrapAsync_VenvCreationFails_ReturnsFalseWithOutput()
    {
        var bootstrapper = new PythonPluginBootstrapper(runner: (psi, _) =>
            Task.FromResult(psi.ArgumentList.Contains("venv")
                ? (1, "python3.11 not found")
                : (0, "")));

        var result = await bootstrapper.BootstrapAsync(_pluginDir, timeoutSeconds: 10);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Be("python3.11 not found");
    }

    [Fact]
    public async Task BootstrapAsync_PipInstallFails_ReturnsFalseWithOutput()
    {
        var bootstrapper = new PythonPluginBootstrapper(runner: (psi, _) =>
            Task.FromResult(psi.ArgumentList.Contains("install")
                ? (1, "No module named mypackage")
                : (0, "")));

        var result = await bootstrapper.BootstrapAsync(_pluginDir, timeoutSeconds: 10);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Be("No module named mypackage");
    }
}
