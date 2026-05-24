// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using NSubstitute;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// NB-7 wiring: the container-plugin lifecycle manager routes high-risk mutations
/// through the approval gate before any registry/host side effect, and proceeds
/// normally when no gate is wired.
/// </summary>
public sealed class ContainerPluginApprovalGateTests
{
    private static (IContainerPluginRegistry registry, IContainerPluginHost host) Fakes()
        => (Substitute.For<IContainerPluginRegistry>(), Substitute.For<IContainerPluginHost>());

    private static ContainerPluginManifest Manifest(string id = "p1")
        => new(id, "1.0") { Spec = new ContainerPluginSpec { Image = $"registry/{id}:1.0" } };

    [Fact]
    public async Task Create_Held_By_Gate_Does_Not_Touch_Registry_Or_Host()
    {
        var (registry, host) = Fakes();
        var gate = new ThrowingGate();
        var manager = new ContainerPluginLifecycleManager(registry, host, approvalGate: gate);

        var act = async () => await manager.CreateAsync(Manifest());

        await act.Should().ThrowAsync<ApprovalRequiredException>();
        gate.SeenKind.Should().Be("ContainerPlugin");
        gate.SeenName.Should().Be("p1");
        await registry.DidNotReceive().RegisterAsync(Arg.Any<ContainerPluginManifest>(), Arg.Any<CancellationToken>());
        await host.DidNotReceive().RegisterAsync(Arg.Any<ContainerPluginManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_Proceeds_Through_Gate_When_Allowed()
    {
        var (registry, host) = Fakes();
        var gate = new AllowGate();
        var manager = new ContainerPluginLifecycleManager(registry, host, approvalGate: gate);

        var handle = await manager.CreateAsync(Manifest());

        handle.Id.Should().Be("p1");
        gate.Calls.Should().ContainSingle().Which.Should().Be("ContainerPlugin/p1");
        await registry.Received(1).RegisterAsync(Arg.Any<ContainerPluginManifest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_Without_Gate_Proceeds()
    {
        var (registry, host) = Fakes();
        var manager = new ContainerPluginLifecycleManager(registry, host); // no approval gate

        var handle = await manager.CreateAsync(Manifest());

        handle.Id.Should().Be("p1");
        await registry.Received(1).RegisterAsync(Arg.Any<ContainerPluginManifest>(), Arg.Any<CancellationToken>());
    }

    private sealed class ThrowingGate : IApprovalGate
    {
        public string? SeenKind;
        public string? SeenName;
        public ValueTask EnsureApprovedAsync(string kind, string name, string manifestCanonical, string requestedBy, CancellationToken ct = default)
        {
            SeenKind = kind;
            SeenName = name;
            throw new ApprovalRequiredException(kind, name, "req-1");
        }
    }

    private sealed class AllowGate : IApprovalGate
    {
        public List<string> Calls { get; } = new();
        public ValueTask EnsureApprovedAsync(string kind, string name, string manifestCanonical, string requestedBy, CancellationToken ct = default)
        {
            Calls.Add($"{kind}/{name}");
            return ValueTask.CompletedTask;
        }
    }
}
