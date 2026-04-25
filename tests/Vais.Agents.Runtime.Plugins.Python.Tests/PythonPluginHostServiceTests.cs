// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.IO.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

/// <summary>
/// Tests for <see cref="PythonPluginHostService"/> using injected fake supervisors.
/// The scanner runs against a real temp directory, but process I/O is fully in-memory.
/// </summary>
public sealed class PythonPluginHostServiceTests : IDisposable
{
    private static readonly TimeSpan[] FastBackoff =
        [TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20)];

    private readonly string _pluginsRoot;
    private readonly CancellationTokenSource _responderCts = new();

    public PythonPluginHostServiceTests()
    {
        _pluginsRoot = Path.Combine(
            Path.GetTempPath(),
            "vais-host-svc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginsRoot);
    }

    public void Dispose()
    {
        _responderCts.Cancel();
        _responderCts.Dispose();
        Directory.Delete(_pluginsRoot, recursive: true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void CreatePluginFolder(string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_pluginsRoot, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "plugin.yaml"), $"""
            apiVersion: vais.agents/v1
            kind: Plugin
            metadata:
              name: {name}
            spec:
              runtime: python
              entrypoint: src/server.py
              python:
                version: "3.13"
                interpreter: .venv/bin/python
              health:
                handshakeTimeoutSeconds: 5
                restartPolicy: exponentialBackoff
            """);
        File.WriteAllText(Path.Combine(folder, "pyproject.toml"), """
            [project]
            name = "placeholder"

            [tool.vais.plugin]
            targetApiVersion = "0.23"
            tools = ["tool_a"]
            """);
    }

    private static (Stream SupervisorInput, Stream SupervisorOutput, Stream ResponderInput, Stream ResponderOutput) MakePipes()
    {
        var c2s = new Pipe();
        var s2c = new Pipe();
        return (c2s.Writer.AsStream(), s2c.Reader.AsStream(), c2s.Reader.AsStream(), s2c.Writer.AsStream());
    }

    private PythonSubprocessSupervisor MakeFakeSupervisor(PythonPluginDescriptor descriptor)
    {
        var (supIn, supOut, respIn, respOut) = MakePipes();
        var handle = new FakeSubprocessHandle(supIn, supOut);
        var responder = new MockMcpResponder(respIn, respOut, descriptor.DeclaredTools.ToList());
        _ = Task.Run(() => responder.RunAsync(_responderCts.Token));
        return new PythonSubprocessSupervisor(descriptor, NullLoggerFactory.Instance, _ => handle, FastBackoff);
    }

    private PythonPluginLoaderOptions MakeOptions() =>
        new()
        {
            PluginsDirectory = _pluginsRoot,
            RuntimeAbiVersion = "0.23",
            DefaultHandshakeTimeoutSeconds = 5,
        };

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_TwoPlugins_BothReady()
    {
        CreatePluginFolder("plugin-a");
        CreatePluginFolder("plugin-b");

        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance, MakeFakeSupervisor);

        await svc.StartAsync(CancellationToken.None);

        svc.LoadedPlugins.Should().HaveCount(2);
        svc.LoadedPlugins.Should().AllSatisfy(p => p.Status.Should().Be(PythonPluginStatus.Ready));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_EmptyDirectory_ZeroPlugins()
    {
        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance, MakeFakeSupervisor);

        await svc.StartAsync(CancellationToken.None);

        svc.LoadedPlugins.Should().BeEmpty();

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LoadedPlugins_EachCallReturnsNewSnapshot()
    {
        CreatePluginFolder("plugin-snap");

        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance, MakeFakeSupervisor);

        await svc.StartAsync(CancellationToken.None);

        var snap1 = svc.LoadedPlugins;
        var snap2 = svc.LoadedPlugins;

        snap1.Should().NotBeSameAs(snap2, "LoadedPlugins must return a new snapshot each call");
        snap1.Should().BeEquivalentTo(snap2);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_BeforeStartAsync_CompletesCleanly()
    {
        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance, MakeFakeSupervisor);

        // StopAsync before StartAsync must not throw.
        var act = async () => await svc.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_OnePlugin_McpClientExposedOnLoadedPlugin()
    {
        CreatePluginFolder("plugin-mcp");

        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance, MakeFakeSupervisor);

        await svc.StartAsync(CancellationToken.None);

        var plugin = svc.LoadedPlugins.Single();
        plugin.McpClient.Should().NotBeNull("a Ready plugin must expose its McpClient");
        plugin.ProcessId.Should().Be(42);

        await svc.StopAsync(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // INamedToolSourceProvider.GetByName
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetByName_ReadyPlugin_Returns_NonNull_Source()
    {
        CreatePluginFolder("plugin-named");

        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance, MakeFakeSupervisor);
        await svc.StartAsync(CancellationToken.None);

        INamedToolSourceProvider provider = svc;
        var source = provider.GetByName("plugin-named");

        source.Should().NotBeNull("a Ready plugin must be reachable by its declared name");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetByName_UnknownName_Returns_Null()
    {
        CreatePluginFolder("plugin-known");

        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance, MakeFakeSupervisor);
        await svc.StartAsync(CancellationToken.None);

        INamedToolSourceProvider provider = svc;
        var source = provider.GetByName("not-a-plugin");

        source.Should().BeNull();

        await svc.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // v0.31 — Secret resolution
    // -----------------------------------------------------------------------

    private void CreatePluginFolderWithSecrets(string name, string secretsYaml)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_pluginsRoot, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "plugin.yaml"), $"""
            apiVersion: vais.agents/v1
            kind: Plugin
            metadata:
              name: {name}
            spec:
              runtime: python
              entrypoint: src/server.py
              python:
                version: "3.13"
                interpreter: .venv/bin/python
              health:
                handshakeTimeoutSeconds: 5
                restartPolicy: exponentialBackoff
            {secretsYaml}
            """);
        File.WriteAllText(Path.Combine(folder, "pyproject.toml"), """
            [project]
            name = "placeholder"

            [tool.vais.plugin]
            targetApiVersion = "0.23"
            tools = ["tool_a"]
            """);
    }

    private sealed class FakeSecretResolver : ISecretResolver
    {
        private readonly Dictionary<string, string> _map;
        internal FakeSecretResolver(Dictionary<string, string> map) => _map = map;

        public ValueTask<string> ResolveAsync(string secretUri, CancellationToken ct = default) =>
            _map.TryGetValue(secretUri, out var v)
                ? ValueTask.FromResult(v)
                : throw new SecretNotFoundException(secretUri);
    }

    [Fact]
    public async Task StartAsync_SecretsDeclared_ResolvedRefsInjectedIntoDescriptor()
    {
        CreatePluginFolderWithSecrets("plugin-secrets",
            "  secrets:\n    MY_KEY: \"secret://env/MY_KEY\"");

        PythonPluginDescriptor? captured = null;
        PythonSubprocessSupervisor CapturingSupervisor(PythonPluginDescriptor d)
        {
            captured = d;
            return MakeFakeSupervisor(d);
        }

        var resolver = new FakeSecretResolver(new() { ["secret://env/MY_KEY"] = "the-value" });
        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance,
            CapturingSupervisor, secretResolver: resolver);

        await svc.StartAsync(CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.SecretRefs.Should().ContainKey("VAIS_SECRET_MY_KEY");
        captured.SecretRefs["VAIS_SECRET_MY_KEY"].Should().Be("the-value");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_SecretResolutionFails_PluginSkipped()
    {
        CreatePluginFolderWithSecrets("plugin-missing-secret",
            "  secrets:\n    MISSING: \"secret://env/DOES_NOT_EXIST\"");

        var resolver = new FakeSecretResolver(new()); // nothing to resolve
        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance,
            MakeFakeSupervisor, secretResolver: resolver);

        await svc.StartAsync(CancellationToken.None);

        svc.LoadedPlugins.Should().BeEmpty("plugin with unresolvable secret must be skipped");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_SecretsWithNoResolver_PluginSkipped()
    {
        CreatePluginFolderWithSecrets("plugin-no-resolver",
            "  secrets:\n    MY_KEY: \"secret://env/SOME_KEY\"");

        // No ISecretResolver passed — secrets can't be resolved.
        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance,
            MakeFakeSupervisor, secretResolver: null);

        await svc.StartAsync(CancellationToken.None);

        svc.LoadedPlugins.Should().BeEmpty("plugin declares secrets but resolver absent");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_NoSecretsDeclared_PluginLoadsWithoutResolver()
    {
        CreatePluginFolder("plugin-no-secrets"); // uses CreatePluginFolder (no secrets block)

        // No resolver needed — plugin has no secrets.
        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance,
            MakeFakeSupervisor, secretResolver: null);

        await svc.StartAsync(CancellationToken.None);

        svc.LoadedPlugins.Should().ContainSingle().Which.Status.Should().Be(PythonPluginStatus.Ready);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetByName_ToolSource_Discovers_DeclaredTools()
    {
        // CreatePluginFolder writes pyproject.toml with tools = ["tool_a"]
        // MockMcpResponder serves exactly those tools via tools/list.
        CreatePluginFolder("plugin-discover");

        var svc = new PythonPluginHostService(MakeOptions(), NullLoggerFactory.Instance, MakeFakeSupervisor);
        await svc.StartAsync(CancellationToken.None);

        INamedToolSourceProvider provider = svc;
        var source = provider.GetByName("plugin-discover");
        source.Should().NotBeNull();

        var tools = new List<ITool>();
        await foreach (var tool in source!.DiscoverAsync())
        {
            tools.Add(tool);
        }

        tools.Should().ContainSingle().Which.Name.Should().Be("tool_a");

        await svc.StopAsync(CancellationToken.None);
    }
}
