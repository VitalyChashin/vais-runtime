// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.IO.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Unit tests for <see cref="DefaultPythonPluginReloader"/>.
/// Scanner is exercised against a real temp directory; supervisors are fully in-memory.
/// </summary>
public sealed class DefaultPythonPluginReloaderTests : IDisposable
{
    private static readonly TimeSpan[] FastBackoff =
        [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)];

    private readonly string _pluginsRoot;

    public DefaultPythonPluginReloaderTests()
    {
        _pluginsRoot = Path.Combine(
            Path.GetTempPath(),
            "vais-reloader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginsRoot);
    }

    public void Dispose() =>
        Directory.Delete(_pluginsRoot, recursive: true);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (PipeSetup Pipes, AgentMockMcpResponder Responder, FakeSubprocessHandle Handle)
        MakeAgentPlugin(int pid = 42)
    {
        var pipes = MakePipes();
        var handle = new FakeSubprocessHandle(pipes.SupervisorInput, pipes.SupervisorOutput, pid);
        var responder = new AgentMockMcpResponder(pipes.ResponderInput, pipes.ResponderOutput);
        return (pipes, responder, handle);
    }

    private static PipeSetup MakePipes()
    {
        var c2s = new Pipe();
        var s2c = new Pipe();
        return new PipeSetup(c2s.Writer.AsStream(), s2c.Reader.AsStream(),
            c2s.Reader.AsStream(), s2c.Writer.AsStream());
    }

    private sealed record PipeSetup(
        Stream SupervisorInput,
        Stream SupervisorOutput,
        Stream ResponderInput,
        Stream ResponderOutput);

    private string CreateAgentPluginDir(string name, string handlerTypeName)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_pluginsRoot, name)).FullName;

        File.WriteAllText(Path.Combine(folder, "plugin.yaml"), $"""
            apiVersion: vais.agents/v1
            kind: Plugin
            metadata:
              name: {name}
            spec:
              runtime: python
              kind: agent-handler
              handler:
                typeName: {handlerTypeName}
              python:
                interpreter: .venv/bin/python
              entrypoint: server.py
            """);

        File.WriteAllText(Path.Combine(folder, "pyproject.toml"), """
            [tool.vais.plugin]
            targetApiVersion = "0.24"
            """);

        // Create a fake interpreter and server script so path validation passes.
        var venvBin = Directory.CreateDirectory(Path.Combine(folder, ".venv", "bin")).FullName;
        var python = Path.Combine(venvBin, "python");
        File.WriteAllText(python, "#!/bin/sh");

        var serverPy = Path.Combine(folder, "server.py");
        File.WriteAllText(serverPy, "# fake");

        return folder;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ReloadAsync_PluginDirDoesNotExist_ReturnsScanFailed()
    {
        var options = new PythonPluginLoaderOptions { PluginsDirectory = _pluginsRoot };
        var host = new PythonPluginHostService(options, NullLoggerFactory.Instance);
        var reloader = new DefaultPythonPluginReloader(
            host, options, TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);

        var result = await reloader.ReloadAsync(Path.Combine(_pluginsRoot, "nonexistent"));

        result.Status.Should().Be(PythonPluginReloadStatus.ScanFailed);
        result.FailureUrn.Should().Be(PythonPluginUrns.ReloadScanFailed);
    }

    [Fact]
    public async Task ReloadAsync_InvalidPluginYaml_ReturnsScanFailed()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_pluginsRoot, "bad-plugin")).FullName;
        File.WriteAllText(Path.Combine(dir, "plugin.yaml"), "not: valid: yaml: {{ }}}}");

        var options = new PythonPluginLoaderOptions { PluginsDirectory = _pluginsRoot };
        var host = new PythonPluginHostService(options, NullLoggerFactory.Instance);
        var reloader = new DefaultPythonPluginReloader(
            host, options, TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);

        var result = await reloader.ReloadAsync(dir);

        result.Status.Should().Be(PythonPluginReloadStatus.ScanFailed);
    }

    [Fact]
    public async Task ReloadAsync_NoSupervisorForPlugin_ReturnsNoSupervisor()
    {
        var dir = CreateAgentPluginDir("my-agent", "MyHandlerType");
        var options = new PythonPluginLoaderOptions { PluginsDirectory = _pluginsRoot };

        // Host has no supervisors (StartAsync was never called).
        var host = new PythonPluginHostService(options, NullLoggerFactory.Instance);
        var reloader = new DefaultPythonPluginReloader(
            host, options, TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);

        var result = await reloader.ReloadAsync(dir);

        result.Status.Should().Be(PythonPluginReloadStatus.NoSupervisor);
        result.FailureUrn.Should().Be(PythonPluginUrns.ReloadNoSupervisor);
        result.PluginName.Should().Be("my-agent");
    }

    [Fact]
    public async Task ReloadAsync_ValidDescriptorAndSupervisor_CallsDrainAndRestartAndReturnsSuccess()
    {
        var dir = CreateAgentPluginDir("live-agent", "LiveHandlerType");
        var options = new PythonPluginLoaderOptions { PluginsDirectory = _pluginsRoot };

        // Set up two process generations (initial + reload).
        var (p1, r1, h1) = MakeAgentPlugin(pid: 100);
        var (p2, r2, h2) = MakeAgentPlugin(pid: 200);

        using var responderCts = new CancellationTokenSource();
        _ = Task.Run(() => r1.RunAsync(responderCts.Token));
        _ = Task.Run(() => r2.RunAsync(responderCts.Token));

        var spawnCount = 0;
        var host = new PythonPluginHostService(
            options,
            NullLoggerFactory.Instance,
            supervisorFactory: d => new PythonSubprocessSupervisor(
                d, NullLoggerFactory.Instance,
                _ => ++spawnCount == 1 ? h1 : h2,
                FastBackoff));

        await host.StartAsync(default);
        host.LoadedPlugins.Should().ContainSingle(p => p.Descriptor.Name == "live-agent");

        var reloader = new DefaultPythonPluginReloader(
            host, options, TimeSpan.FromSeconds(5), NullLoggerFactory.Instance);

        var result = await reloader.ReloadAsync(dir).WaitAsync(TimeSpan.FromSeconds(15));

        result.Status.Should().Be(PythonPluginReloadStatus.Success);
        spawnCount.Should().Be(2);

        await responderCts.CancelAsync();
        await host.StopAsync(default);
    }
}
