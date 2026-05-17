// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// CMS-1: <c>transport: containerStdio</c> + <c>spec.container.*</c> parse and validate
/// per <c>plans/mcp-stdio-native-impl-2026-05-17.md</c>.
/// </summary>
public sealed class ContainerMcpServerLoaderTests
{
    private static readonly YamlAgentGraphManifestLoader Loader = new();

    private const string MinimalImageYaml = """
        apiVersion: vais.agents/v1
        kind: McpServer
        metadata:
          id: mcp-fetch
          version: "1.0"
        spec:
          transport: containerStdio
          container:
            image: my-mcp/fetch:1.0
        """;

    [Fact]
    public async Task ContainerStdio_With_Image_Parses()
    {
        var resources = await Loader.LoadAllResourcesFromStringAsync(MinimalImageYaml);

        var srv = ((ManifestResource.McpServerCase)resources.Single()).Server;
        srv.Transport.Should().Be("containerStdio");
        srv.Container.Should().NotBeNull();
        srv.Container!.Image.Should().Be("my-mcp/fetch:1.0");
        srv.Container.Port.Should().Be(7000);
        srv.Container.Path.Should().Be("/mcp");
        srv.Container.HealthPath.Should().Be("/health");
        srv.Container.StartupTimeoutSeconds.Should().Be(30);
        srv.Container.ImagePullPolicy.Should().Be("IfNotPresent");
        srv.Container.Build.Should().BeNull();
    }

    [Fact]
    public async Task ContainerStdio_With_Build_Parses()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata:
              id: mcp-fetch
              version: "1.0"
            spec:
              transport: containerStdio
              container:
                build:
                  context: ./mcp-fetch
                  dockerfile: Dockerfile
                env:
                  MCP_STDIO_CMD: "python -m mcp_server_fetch"
            """;

        var resources = await Loader.LoadAllResourcesFromStringAsync(yaml);

        var srv = ((ManifestResource.McpServerCase)resources.Single()).Server;
        srv.Container!.Build.Should().NotBeNull();
        srv.Container.Build!.Context.Should().Be("./mcp-fetch");
        srv.Container.Build.Dockerfile.Should().Be("Dockerfile");
        srv.Container.Env!["MCP_STDIO_CMD"].Should().Be("python -m mcp_server_fetch");
    }

    [Fact]
    public async Task ContainerStdio_With_All_Fields_Parses()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata:
              id: mcp-fetch
              version: "1.0"
            spec:
              transport: containerStdio
              mcpGatewayRef: demo-gw
              container:
                image: my-mcp/fetch:2.0
                port: 7100
                path: /custom-mcp
                healthPath: /ready
                command: ["python", "bridge.py"]
                args: ["--verbose"]
                env:
                  FOO: bar
                secrets:
                  API_TOKEN: secret://env/MY_TOKEN
                startupTimeoutSeconds: 60
                imagePullPolicy: Always
                resources:
                  memory: 256Mi
                  cpu: "0.5"
                  pidsLimit: 100
                kubernetes:
                  serviceUrl: http://mcp-fetch.default.svc.cluster.local:7100
                  deploymentName: mcp-fetch
                  namespace: tools
            """;

        var resources = await Loader.LoadAllResourcesFromStringAsync(yaml);

        var srv = ((ManifestResource.McpServerCase)resources.Single()).Server;
        var c = srv.Container!;
        c.Port.Should().Be(7100);
        c.Path.Should().Be("/custom-mcp");
        c.HealthPath.Should().Be("/ready");
        c.Command.Should().Equal("python", "bridge.py");
        c.Args.Should().Equal("--verbose");
        c.Env!["FOO"].Should().Be("bar");
        c.Secrets!["API_TOKEN"].Should().Be("secret://env/MY_TOKEN");
        c.StartupTimeoutSeconds.Should().Be(60);
        c.ImagePullPolicy.Should().Be("Always");
        c.Resources!.Memory.Should().Be("256Mi");
        c.Resources.Cpu.Should().Be("0.5");
        c.Resources.PidsLimit.Should().Be(100);
        c.Kubernetes!.ServiceUrl.Should().Be("http://mcp-fetch.default.svc.cluster.local:7100");
        c.Kubernetes.DeploymentName.Should().Be("mcp-fetch");
        c.Kubernetes.Namespace.Should().Be("tools");
        srv.McpGatewayRef.Should().Be("demo-gw");
    }

    [Fact]
    public async Task ContainerStdio_Without_Container_Block_Fails()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: x, version: "1.0" }
            spec: { transport: containerStdio }
            """;

        var act = async () => await Loader.LoadAllResourcesFromStringAsync(yaml);

        (await act.Should().ThrowAsync<AgentManifestValidationException>())
            .Which.Errors.Should().Contain(e => e.Contains("spec.container is required"));
    }

    [Fact]
    public async Task Container_With_NonContainerStdio_Transport_Fails()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: x, version: "1.0" }
            spec:
              transport: streamableHttp
              url: http://localhost:9000/mcp
              container:
                image: foo:1.0
            """;

        var act = async () => await Loader.LoadAllResourcesFromStringAsync(yaml);

        (await act.Should().ThrowAsync<AgentManifestValidationException>())
            .Which.Errors.Should().Contain(e => e.Contains("spec.container is only valid"));
    }

    [Fact]
    public async Task Image_And_Build_Mutually_Exclusive()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: x, version: "1.0" }
            spec:
              transport: containerStdio
              container:
                image: foo:1.0
                build: { context: ./x }
            """;

        var act = async () => await Loader.LoadAllResourcesFromStringAsync(yaml);

        (await act.Should().ThrowAsync<AgentManifestValidationException>())
            .Which.Errors.Should().Contain(e => e.Contains("mutually exclusive"));
    }

    [Fact]
    public async Task Neither_Image_Nor_Build_Fails()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: x, version: "1.0" }
            spec:
              transport: containerStdio
              container:
                port: 7000
            """;

        var act = async () => await Loader.LoadAllResourcesFromStringAsync(yaml);

        (await act.Should().ThrowAsync<AgentManifestValidationException>())
            .Which.Errors.Should().Contain(e => e.Contains("exactly one of image or build is required"));
    }

    [Fact]
    public async Task Port_Out_Of_Range_Fails()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: x, version: "1.0" }
            spec:
              transport: containerStdio
              container:
                image: foo:1.0
                port: 80
            """;

        var act = async () => await Loader.LoadAllResourcesFromStringAsync(yaml);

        (await act.Should().ThrowAsync<AgentManifestValidationException>())
            .Which.Errors.Should().Contain(e => e.Contains("port must be in [1024, 65535]"));
    }

    [Fact]
    public async Task StartupTimeout_Out_Of_Range_Fails()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: x, version: "1.0" }
            spec:
              transport: containerStdio
              container:
                image: foo:1.0
                startupTimeoutSeconds: 0
            """;

        var act = async () => await Loader.LoadAllResourcesFromStringAsync(yaml);

        (await act.Should().ThrowAsync<AgentManifestValidationException>())
            .Which.Errors.Should().Contain(e => e.Contains("startupTimeoutSeconds must be in [1, 600]"));
    }

    [Fact]
    public async Task Virtual_With_Container_Fails()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: x, version: "1.0" }
            spec:
              virtual: true
              sources: [{ ref: another }]
              container:
                image: foo:1.0
            """;

        var act = async () => await Loader.LoadAllResourcesFromStringAsync(yaml);

        (await act.Should().ThrowAsync<AgentManifestValidationException>())
            .Which.Errors.Should().Contain(e => e.Contains("spec.container must be absent for virtual servers"));
    }

    [Fact]
    public async Task Existing_StreamableHttp_Without_Container_Still_Works()
    {
        // Regression: additive change must not break existing transports.
        var yaml = """
            apiVersion: vais.agents/v1
            kind: McpServer
            metadata: { id: x, version: "1.0" }
            spec:
              transport: streamableHttp
              url: http://localhost:9000/mcp
            """;

        var resources = await Loader.LoadAllResourcesFromStringAsync(yaml);

        var srv = ((ManifestResource.McpServerCase)resources.Single()).Server;
        srv.Transport.Should().Be("streamableHttp");
        srv.Container.Should().BeNull();
    }
}
