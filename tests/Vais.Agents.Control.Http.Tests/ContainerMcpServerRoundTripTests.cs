// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// CMS-1 round-trip guard — <c>McpServerManifest.Container</c> survives
/// <see cref="EnvelopeSerializer.Serialize(Vais.Agents.McpServerManifest)"/> →
/// <see cref="JsonAgentGraphManifestLoader.LoadAllResourcesFromStringAsync"/>
/// without dropped fields. Matches the pattern from
/// <c>feedback_manifest_di_recurring_gaps</c>.
/// </summary>
public sealed class ContainerMcpServerRoundTripTests
{
    private static readonly JsonAgentGraphManifestLoader Loader = new();

    [Fact]
    public async Task Container_Fully_Populated_RoundTrips()
    {
        var original = new McpServerManifest("mcp-fetch", "1.0", Description: "fetch tool")
        {
            Transport = "containerStdio",
            McpGatewayRef = "demo-gw",
            Container = new ContainerMcpSpec
            {
                Image = "my-mcp/fetch:2.0",
                Port = 7100,
                Path = "/custom-mcp",
                HealthPath = "/ready",
                Command = new[] { "python", "bridge.py" },
                Args = new[] { "--verbose" },
                Env = new Dictionary<string, string> { ["FOO"] = "bar" },
                Secrets = new Dictionary<string, string> { ["TOKEN"] = "secret://env/TOK" },
                StartupTimeoutSeconds = 60,
                ImagePullPolicy = "Always",
                Resources = new ContainerMcpResources { Memory = "256Mi", Cpu = "0.5", PidsLimit = 100 },
                Kubernetes = new ContainerMcpKubernetesConfig
                {
                    ServiceUrl = "http://mcp-fetch.default.svc.cluster.local:7100",
                    DeploymentName = "mcp-fetch",
                    Namespace = "tools",
                },
            },
        };

        var json = EnvelopeSerializer.Serialize(original);
        var resources = await Loader.LoadAllResourcesFromStringAsync(json);
        var srv = ((ManifestResource.McpServerCase)resources.Single()).Server;

        srv.Transport.Should().Be("containerStdio");
        srv.McpGatewayRef.Should().Be("demo-gw");
        srv.Container.Should().BeEquivalentTo(original.Container);
    }

    [Fact]
    public async Task Container_With_Build_RoundTrips()
    {
        var original = new McpServerManifest("mcp-foo", "1.0")
        {
            Transport = "containerStdio",
            Container = new ContainerMcpSpec
            {
                Build = new ContainerMcpBuildSpec
                {
                    Context = "./mcp-foo",
                    Dockerfile = "Dockerfile.custom",
                    Args = new Dictionary<string, string> { ["VERSION"] = "1.0" },
                    Push = true,
                },
            },
        };

        var json = EnvelopeSerializer.Serialize(original);
        var resources = await Loader.LoadAllResourcesFromStringAsync(json);
        var srv = ((ManifestResource.McpServerCase)resources.Single()).Server;

        srv.Container.Should().BeEquivalentTo(original.Container);
    }

    [Fact]
    public async Task Container_Minimal_RoundTrips()
    {
        var original = new McpServerManifest("mcp-bar", "1.0")
        {
            Transport = "containerStdio",
            Container = new ContainerMcpSpec { Image = "bar:1.0" },
        };

        var json = EnvelopeSerializer.Serialize(original);
        var resources = await Loader.LoadAllResourcesFromStringAsync(json);
        var srv = ((ManifestResource.McpServerCase)resources.Single()).Server;

        srv.Container!.Image.Should().Be("bar:1.0");
        srv.Container.Port.Should().Be(7000);             // default preserved
        srv.Container.Path.Should().Be("/mcp");           // default preserved
        srv.Container.HealthPath.Should().Be("/health");  // default preserved
    }

    [Fact]
    public async Task NonContainer_McpServer_RoundTrip_Unaffected()
    {
        // Regression: existing transports must still round-trip cleanly.
        var original = new McpServerManifest("mcp-http", "1.0")
        {
            Transport = "streamableHttp",
            Url = "http://localhost:9000/mcp",
        };

        var json = EnvelopeSerializer.Serialize(original);
        var resources = await Loader.LoadAllResourcesFromStringAsync(json);
        var srv = ((ManifestResource.McpServerCase)resources.Single()).Server;

        srv.Transport.Should().Be("streamableHttp");
        srv.Url.Should().Be("http://localhost:9000/mcp");
        srv.Container.Should().BeNull();
    }
}
