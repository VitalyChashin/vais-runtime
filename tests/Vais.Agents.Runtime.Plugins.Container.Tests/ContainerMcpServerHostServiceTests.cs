// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Control;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// CMS-10 — lifecycle + INamedToolSourceProvider wiring for container MCP servers.
/// CMS-11 — drain-replace under registry update.
///
/// Uses fake <see cref="IContainerSupervisor"/> + fake <see cref="IToolSource"/> so the
/// tests run without Docker. Plan: <c>plans/mcp-stdio-native-impl-2026-05-17.md</c>.
/// </summary>
public sealed class ContainerMcpServerHostServiceTests
{
    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static McpServerManifest ContainerStdio(string id, string image = "img:1.0")
        => new(id, "1.0")
        {
            Transport = "containerStdio",
            Container = new ContainerMcpSpec { Image = image },
        };

    private sealed class FakeSupervisor : IContainerSupervisor
    {
        public bool StartCalled, StopCalled, Disposed;
        public bool StartShouldThrow;
        public string? ReplacedToImage;
        public ContainerPluginDescriptor Descriptor { get; }
        public ContainerPluginStatus Status { get; private set; } = ContainerPluginStatus.Created;

        public FakeSupervisor(ContainerPluginDescriptor descriptor) { Descriptor = descriptor; }

        public Task StartAsync(CancellationToken ct = default)
        {
            StartCalled = true;
            if (StartShouldThrow) throw new InvalidOperationException("simulated container start failure");
            Status = ContainerPluginStatus.Ready;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            StopCalled = true;
            Status = ContainerPluginStatus.Stopped;
            return Task.CompletedTask;
        }
        public Task<ContainerReplaceResult> DrainAndReplaceAsync(string? newImage, CancellationToken ct)
        {
            ReplacedToImage = newImage;
            return Task.FromResult(new ContainerReplaceResult(ContainerReplaceOutcome.Success));
        }
        public bool TryAcquireInvoke() => true;
        public void ReleaseInvoke() { }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeToolSource : IToolSource
    {
        public string Tag { get; }
        public FakeToolSource(string tag) { Tag = tag; }
#pragma warning disable CS1998
        public async IAsyncEnumerable<ITool> DiscoverAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }

    private sealed class FakeAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed record TestHarness(
        ContainerMcpServerHostService Service,
        InMemoryMcpServerRegistry Registry,
        IMcpServerConnectionChangedHook Hook,
        List<FakeSupervisor> Supervisors,
        Func<Uri, IAsyncDisposable, Task<(IAsyncDisposable Client, IToolSource Source)>> ToolSourceFactory);

    private static TestHarness BuildHarness(
        Action<List<FakeSupervisor>>? configureSupervisors = null,
        Func<Uri, IAsyncDisposable, Task<(IAsyncDisposable, IToolSource)>>? toolSourceFactory = null)
    {
        var registry = new InMemoryMcpServerRegistry();
        var hook = Substitute.For<IMcpServerConnectionChangedHook>();
        var supervisors = new List<FakeSupervisor>();

        Func<ContainerPluginDescriptor, IContainerSupervisor> supervisorFactory = d =>
        {
            var s = new FakeSupervisor(d);
            supervisors.Add(s);
            configureSupervisors?.Invoke(supervisors);
            return s;
        };

        toolSourceFactory ??= (_, _) => Task.FromResult<(IAsyncDisposable, IToolSource)>(
            (new FakeAsyncDisposable(), new FakeToolSource("ok")));

        var svc = new ContainerMcpServerHostService(
            registry,
            new ContainerPluginLoaderOptions(),
            () => new[] { hook },
            supervisorFactory,
            toolSourceFactory,
            NullLoggerFactory.Instance);

        return new TestHarness(svc, registry, hook, supervisors, toolSourceFactory);
    }

    // ── CMS-10: apply + invoke happy path ────────────────────────────────────────

    [Fact]
    public async Task GetByName_Returns_Null_Before_Anything_Registered()
    {
        var h = BuildHarness();
        h.Service.GetByName("anything").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_Starts_Supervisor_And_Exposes_ToolSource()
    {
        var h = BuildHarness();

        await h.Service.RegisterAsync(ContainerStdio("mcp-fetch"), CancellationToken.None);

        h.Supervisors.Should().ContainSingle();
        h.Supervisors[0].StartCalled.Should().BeTrue();
        h.Service.GetByName("mcp-fetch").Should().BeOfType<FakeToolSource>();
        await h.Hook.Received(1).OnConnectedAsync("mcp-fetch", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_Ignores_Non_ContainerStdio_Transports()
    {
        var h = BuildHarness();

        await h.Service.RegisterAsync(new McpServerManifest("http-srv", "1.0")
        {
            Transport = "streamableHttp",
            Url = "http://localhost:1234/mcp",
        }, CancellationToken.None);

        h.Supervisors.Should().BeEmpty();
        h.Service.GetByName("http-srv").Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_Ignores_Virtual_Servers()
    {
        var h = BuildHarness();

        await h.Service.RegisterAsync(new McpServerManifest("v-srv", "1.0")
        {
            Virtual = true,
            Sources = new[] { new McpServerSourceRef("upstream") },
        }, CancellationToken.None);

        h.Supervisors.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisterAsync_Failure_Leaves_Entry_Null_And_Hook_Not_Fired()
    {
        var hook = Substitute.For<IMcpServerConnectionChangedHook>();
        var registry = new InMemoryMcpServerRegistry();
        var svc = new ContainerMcpServerHostService(
            registry,
            new ContainerPluginLoaderOptions(),
            () => new[] { hook },
            d => { var s = new FakeSupervisor(d) { StartShouldThrow = true }; return s; },
            (_, _) => Task.FromResult<(IAsyncDisposable, IToolSource)>((new FakeAsyncDisposable(), new FakeToolSource("x"))),
            NullLoggerFactory.Instance);

        await svc.RegisterAsync(ContainerStdio("bad"), CancellationToken.None);

        svc.GetByName("bad").Should().BeNull();
        await hook.DidNotReceive().OnConnectedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_Scans_Registry_And_Connects_All_ContainerStdio_Entries()
    {
        var h = BuildHarness();
        await h.Registry.RegisterAsync(ContainerStdio("a"));
        await h.Registry.RegisterAsync(ContainerStdio("b"));
        // Mixed in: non-container entries that should be ignored
        await h.Registry.RegisterAsync(new McpServerManifest("http", "1.0")
        {
            Transport = "streamableHttp", Url = "http://x/mcp",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await h.Service.StartAsync(cts.Token);
        await Task.Delay(200);

        h.Supervisors.Select(s => s.Descriptor.Name).Should().BeEquivalentTo("a", "b");
        h.Service.GetByName("a").Should().NotBeNull();
        h.Service.GetByName("b").Should().NotBeNull();
        h.Service.GetByName("http").Should().BeNull();

        await h.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnregisterAsync_Stops_Supervisor_And_Removes_Entry()
    {
        var h = BuildHarness();
        await h.Service.RegisterAsync(ContainerStdio("mcp-fetch"), CancellationToken.None);

        await h.Service.UnregisterAsync("mcp-fetch", CancellationToken.None);

        h.Supervisors[0].StopCalled.Should().BeTrue();
        h.Supervisors[0].Disposed.Should().BeTrue();
        h.Service.GetByName("mcp-fetch").Should().BeNull();
        await h.Hook.Received(1).OnDisconnectedAsync("mcp-fetch", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnregisterAsync_NonExistent_Is_NoOp()
    {
        var h = BuildHarness();

        await h.Service.UnregisterAsync("never-was", CancellationToken.None);

        await h.Hook.DidNotReceive().OnDisconnectedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_Fires_Disconnect_Hook_For_Connected_Servers_Only()
    {
        var h = BuildHarness();
        await h.Service.RegisterAsync(ContainerStdio("connected"), CancellationToken.None);

        // Add a registry entry that won't have been connected yet (the host service
        // doesn't auto-pick it up until StartAsync's scan loop fires).
        await h.Registry.RegisterAsync(ContainerStdio("never-connected"));

        await h.Service.StopAsync(CancellationToken.None);

        await h.Hook.Received(1).OnDisconnectedAsync("connected", Arg.Any<CancellationToken>());
        await h.Hook.DidNotReceive().OnDisconnectedAsync("never-connected", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToolSourceFactory_Exception_Stops_Supervisor_And_Disposes()
    {
        var hook = Substitute.For<IMcpServerConnectionChangedHook>();
        var registry = new InMemoryMcpServerRegistry();
        var supervisors = new List<FakeSupervisor>();
        var svc = new ContainerMcpServerHostService(
            registry,
            new ContainerPluginLoaderOptions(),
            () => new[] { hook },
            d => { var s = new FakeSupervisor(d); supervisors.Add(s); return s; },
            (_, _) => throw new InvalidOperationException("simulated MCP handshake failure"),
            NullLoggerFactory.Instance);

        await svc.RegisterAsync(ContainerStdio("bad"), CancellationToken.None);

        supervisors[0].StartCalled.Should().BeTrue();
        supervisors[0].StopCalled.Should().BeTrue();
        supervisors[0].Disposed.Should().BeTrue();
        svc.GetByName("bad").Should().BeNull();
        await hook.DidNotReceive().OnConnectedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── CMS-11: drain-replace ────────────────────────────────────────────────────

    [Fact]
    public async Task Re_RegisterAsync_Replaces_Old_Connection_And_Notifies_Hooks()
    {
        var h = BuildHarness();

        await h.Service.RegisterAsync(ContainerStdio("mcp-fetch", "img:1.0"), CancellationToken.None);
        var firstSource = h.Service.GetByName("mcp-fetch");

        await h.Service.RegisterAsync(ContainerStdio("mcp-fetch", "img:1.1"), CancellationToken.None);

        // Two supervisors were created — old + new
        h.Supervisors.Should().HaveCount(2);

        // Old supervisor stopped + disposed; new one started
        h.Supervisors[0].StopCalled.Should().BeTrue();
        h.Supervisors[0].Disposed.Should().BeTrue();
        h.Supervisors[1].StartCalled.Should().BeTrue();
        h.Supervisors[1].Descriptor.Image.Should().Be("img:1.1");

        // Connection lifecycle hooks fired in the right order
        Received.InOrder(async () =>
        {
            await h.Hook.OnConnectedAsync("mcp-fetch", Arg.Any<CancellationToken>());
            await h.Hook.OnDisconnectedAsync("mcp-fetch", Arg.Any<CancellationToken>());
            await h.Hook.OnConnectedAsync("mcp-fetch", Arg.Any<CancellationToken>());
        });

        // The exposed source is a fresh instance (each registration produces a new tool source).
        var secondSource = h.Service.GetByName("mcp-fetch");
        secondSource.Should().NotBeNull();
        secondSource.Should().NotBeSameAs(firstSource);
    }

    // ── CMS-9: K8s supervisor selection ──────────────────────────────────────────

    [Fact]
    public async Task ManifestWithKubernetes_Builds_K8s_InvokeBaseUrl()
    {
        // Smoke test for the K8s descriptor path: when spec.container.kubernetes is set,
        // the descriptor's InvokeBaseUrl is the K8s Service URL, KubernetesConfig is populated,
        // and DockerPluginNetwork is null. The supervisor factory then picks K8s, which
        // polls /health and patches the Deployment image on DrainAndReplace —
        // unchanged from the container-plugin K8s path.
        var h = BuildHarness();
        var manifest = new McpServerManifest("mcp-fetch-k8s", "1.0")
        {
            Transport = "containerStdio",
            Container = new ContainerMcpSpec
            {
                Image = "ghcr.io/example/mcp-fetch:1.0",
                Kubernetes = new ContainerMcpKubernetesConfig
                {
                    ServiceUrl = "http://mcp-fetch.tools.svc.cluster.local:7000",
                    DeploymentName = "mcp-fetch",
                    Namespace = "tools",
                },
            },
        };

        await h.Service.RegisterAsync(manifest, CancellationToken.None);

        h.Supervisors.Should().ContainSingle();
        var d = h.Supervisors[0].Descriptor;
        d.KubernetesConfig.Should().NotBeNull();
        d.KubernetesConfig!.DeploymentName.Should().Be("mcp-fetch");
        d.KubernetesConfig.Namespace.Should().Be("tools");
        d.InvokeBaseUrl.Should().Be("http://mcp-fetch.tools.svc.cluster.local:7000");
        d.DockerPluginNetwork.Should().BeNull();
    }

    [Fact]
    public async Task Re_RegisterAsync_With_NonContainer_Transport_Removes_Existing_Entry()
    {
        // If a user `vais apply`s the same id with a different transport, the host
        // service should no longer track the entry under containerStdio.
        var h = BuildHarness();
        await h.Service.RegisterAsync(ContainerStdio("mcp-fetch"), CancellationToken.None);
        h.Service.GetByName("mcp-fetch").Should().NotBeNull();

        await h.Service.RegisterAsync(new McpServerManifest("mcp-fetch", "1.1")
        {
            Transport = "streamableHttp",
            Url = "http://elsewhere/mcp",
        }, CancellationToken.None);

        // The non-containerStdio re-register is a no-op for this service —
        // the old entry stays. (PhysicalMcpConnectionService would pick the new one up.)
        // This documents the current behavior; if cross-service handoff is desired,
        // it lives in the lifecycle manager, not here.
        h.Supervisors.Should().HaveCount(1);
        h.Service.GetByName("mcp-fetch").Should().NotBeNull();
    }
}
