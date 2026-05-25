// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// CMT-2 — verifies that DockerContainerSupervisor injects OTLP and structured-log env vars
/// when callTokenService + endpoints are provided (the fix for the container-MCP telemetry gap).
/// Does not hit Docker: mocks IDockerClient; uses StartupTimeoutSeconds=0 so the health check
/// times out immediately after CreateContainerAsync is captured.
/// </summary>
public sealed class ContainerMcpServerOtlpEnvTests
{
    private const string OtlpEndpoint = "http://runtime:5001/v1/otlp";
    private const string LogEndpoint  = "http://runtime:5001/v1/logs";
    private const string FakeToken    = "test-hmac-token";

    private static (DockerContainerSupervisor Supervisor, IContainerOperations Containers)
        BuildSupervisor(ICallTokenService? callTokenService, string? otlpEndpointUrl, string? logEndpointUrl)
    {
        var containers = Substitute.For<IContainerOperations>();
        containers
            .ListContainersAsync(Arg.Any<ContainersListParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IList<ContainerListResponse>>(new List<ContainerListResponse>()));
        containers
            .CreateContainerAsync(Arg.Any<CreateContainerParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CreateContainerResponse { ID = "fake-container-id" }));
        containers
            .StartContainerAsync(Arg.Any<string>(), Arg.Any<ContainerStartParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var docker = Substitute.For<IDockerClient>();
        docker.Containers.Returns(containers);

        var descriptor = new ContainerPluginDescriptor
        {
            Name = "my-mcp",
            Image = "my-mcp:1.0",
            Port = 7000,
            InvokeBaseUrl = "http://127.0.0.1:7000",
            // Zero timeout → WaitForHealthAsync exits immediately without HTTP calls.
            StartupTimeoutSeconds = 0,
        };

        var supervisor = new DockerContainerSupervisor(
            descriptor, docker, NullLogger.Instance,
            callTokenService, otlpEndpointUrl, logEndpointUrl);

        return (supervisor, containers);
    }

    [Fact]
    public async Task StartAsync_WithOtlpEndpoint_InjectsAllOtelEnvVars()
    {
        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
                .Returns(FakeToken);

        var (supervisor, containers) = BuildSupervisor(tokenSvc, OtlpEndpoint, LogEndpoint);

        // StartAsync throws TimeoutException (health check, StartupTimeoutSeconds=0),
        // but CreateContainerAsync has already been called with the env vars.
        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.StartAsync(default));

        await containers.Received(1).CreateContainerAsync(
            Arg.Is<CreateContainerParameters>(p =>
                p.Env.Contains($"OTEL_EXPORTER_OTLP_ENDPOINT={OtlpEndpoint}") &&
                p.Env.Contains("OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf") &&
                p.Env.Any(e => e == $"OTEL_EXPORTER_OTLP_HEADERS=Authorization=vais-plugin-token {FakeToken}") &&
                p.Env.Contains("OTEL_RESOURCE_ATTRIBUTES=vais.agent_id=my-mcp") &&
                p.Env.Any(e => e.StartsWith("VAIS_LOG_ENDPOINT=")) &&
                p.Env.Any(e => e == $"VAIS_LOG_TOKEN={FakeToken}")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WithoutOtlpEndpoint_OmitsOtelEnvVars()
    {
        var (supervisor, containers) = BuildSupervisor(
            callTokenService: null, otlpEndpointUrl: null, logEndpointUrl: null);

        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.StartAsync(default));

        await containers.Received(1).CreateContainerAsync(
            Arg.Is<CreateContainerParameters>(p =>
                !p.Env.Any(e => e.StartsWith("OTEL_")) &&
                !p.Env.Any(e => e.StartsWith("VAIS_LOG_"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_WithTokenServiceButNoEndpoint_OmitsOtelEnvVars()
    {
        var tokenSvc = Substitute.For<ICallTokenService>();
        var (supervisor, containers) = BuildSupervisor(tokenSvc, otlpEndpointUrl: null, logEndpointUrl: null);

        await Assert.ThrowsAsync<TimeoutException>(() => supervisor.StartAsync(default));

        await containers.Received(1).CreateContainerAsync(
            Arg.Is<CreateContainerParameters>(p =>
                !p.Env.Any(e => e.StartsWith("OTEL_")) &&
                !p.Env.Any(e => e.StartsWith("VAIS_LOG_"))),
            Arg.Any<CancellationToken>());
    }
}
