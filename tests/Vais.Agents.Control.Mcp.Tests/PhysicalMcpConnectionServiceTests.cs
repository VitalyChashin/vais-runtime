// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Control;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Control.Mcp.Tests;

public sealed class PhysicalMcpConnectionServiceTests
{
    // --- GetByName ---

    [Fact]
    public void GetByName_Returns_Null_For_Untracked_Server()
    {
        var registry = new InMemoryMcpServerRegistry();
        var svc = new PhysicalMcpConnectionService(registry, [], NullLogger<PhysicalMcpConnectionService>.Instance);

        svc.GetByName("nonexistent").Should().BeNull();
    }

    // --- Connection failure (T2) ---

    [Fact]
    public async Task GetByName_Returns_Null_When_Server_Unreachable()
    {
        var registry = new InMemoryMcpServerRegistry();
        await registry.RegisterAsync(new McpServerManifest("srv-unreachable", "1.0")
        {
            Transport = "streamableHttp",
            Url = "http://localhost:19999/mcp",   // nothing listening here
        });

        var hook = Substitute.For<IMcpServerConnectionChangedHook>();
        var svc = new PhysicalMcpConnectionService(
            registry, [hook], NullLogger<PhysicalMcpConnectionService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);

        // Give the fire-and-forget connection attempt time to complete (fail).
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        svc.GetByName("srv-unreachable").Should().BeNull();
        await hook.DidNotReceive().OnConnectedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        await svc.StopAsync(CancellationToken.None);
    }

    // --- Unsupported transport filtering ---

    [Fact]
    public async Task Virtual_Servers_Are_Not_Tracked()
    {
        var registry = new InMemoryMcpServerRegistry();
        await registry.RegisterAsync(new McpServerManifest("virtual-srv", "1.0")
        {
            Virtual = true,
            Sources = [new McpServerSourceRef("upstream")],
        });

        var svc = new PhysicalMcpConnectionService(
            registry, [], NullLogger<PhysicalMcpConnectionService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        svc.GetByName("virtual-srv").Should().BeNull();

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Servers_Without_Url_Are_Not_Tracked()
    {
        var registry = new InMemoryMcpServerRegistry();
        await registry.RegisterAsync(new McpServerManifest("no-url-srv", "1.0")
        {
            Transport = "streamableHttp",
            Url = null,
        });

        var svc = new PhysicalMcpConnectionService(
            registry, [], NullLogger<PhysicalMcpConnectionService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        svc.GetByName("no-url-srv").Should().BeNull();

        await svc.StopAsync(CancellationToken.None);
    }

    // --- Hook dispatching on stop ---

    [Fact]
    public async Task StopAsync_Does_Not_Dispatch_Disconnected_Hook_For_Never_Connected_Server()
    {
        var registry = new InMemoryMcpServerRegistry();
        await registry.RegisterAsync(new McpServerManifest("srv-down", "1.0")
        {
            Transport = "streamableHttp",
            Url = "http://localhost:19998/mcp",
        });

        var hook = Substitute.For<IMcpServerConnectionChangedHook>();
        var svc = new PhysicalMcpConnectionService(
            registry, [hook], NullLogger<PhysicalMcpConnectionService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(600)); // let connection fail

        await svc.StopAsync(CancellationToken.None);

        // OnDisconnectedAsync should only fire for servers that were previously connected.
        await hook.DidNotReceive().OnDisconnectedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
