// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Round-trip completeness guard for <see cref="McpServerManifest"/> — every optional
/// field (incl. the nested <c>ContainerMcpSpec</c> → build / resources / kubernetes tree)
/// must survive <c>EnvelopeSerializer.Serialize(McpServerManifest)</c> →
/// <see cref="JsonAgentGraphManifestLoader"/> without being dropped.
/// </summary>
/// <remarks>
/// Unlike the graph guard's single rich fixture, McpServer has three mutually-exclusive
/// shapes (physical HTTP/stdio, <c>containerStdio</c>, virtual) and an image-XOR-build
/// rule, so each row carries its own valid manifest — the per-field style of the flat
/// M1.3 walker. The shared <see cref="ManifestRoundTripWalker"/> supplies the coverage
/// check.
/// </remarks>
public sealed class McpServerManifestFieldRoundTripTests
{
    private static McpServerManifest Physical() => new("mcp-http", "1.0")
    {
        Transport = "streamableHttp",
        Url = "http://localhost:9000/mcp",
    };

    private static McpServerManifest Stdio() => new("mcp-stdio", "1.0")
    {
        Transport = "stdio",
        Command = "/usr/bin/srv",
    };

    private static McpServerManifest Virtual() => new("mcp-virtual", "1.0")
    {
        Virtual = true,
        Sources = new[] { new McpServerSourceRef("up") },
    };

    private static McpServerManifest VirtualProj() => Virtual() with
    {
        ToolProjection = new[] { new McpServerToolProjection("vname", "up", "orig") },
    };

    private static McpServerManifest Container(ContainerMcpSpec spec) => new("mcp-container", "1.0")
    {
        Transport = "containerStdio",
        Container = spec,
    };

    private static ContainerMcpSpec ImageSpec() => new() { Image = "img:1" };
    private static McpServerManifest BuildContainer(ContainerMcpBuildSpec build) =>
        Container(new ContainerMcpSpec { Build = build });

    private static object?[] Row(string path, McpServerManifest manifest, Func<McpServerManifest, object?> extract, object? expected)
        => [path, manifest, extract, expected];

    public static IEnumerable<object?[]> RoundTripCases()
    {
        yield return Row("Description", Physical() with { Description = "fetch tool" }, m => m.Description, "fetch tool");
        yield return Row("Labels", Physical() with { Labels = new Dictionary<string, string> { ["team"] = "platform" } }, m => m.Labels!["team"], "platform");
        yield return Row("Annotations", Physical() with { Annotations = new Dictionary<string, string> { ["owner"] = "vais" } }, m => m.Annotations!["owner"], "vais");
        yield return Row("Transport", Physical(), m => m.Transport, "streamableHttp");
        yield return Row("Url", Physical(), m => m.Url, "http://localhost:9000/mcp");
        yield return Row("Command", Stdio(), m => m.Command, "/usr/bin/srv");
        yield return Row("Args", Stdio() with { Args = new[] { "--flag" } }, m => m.Args!.Single(), "--flag");
        yield return Row("Env", Stdio() with { Env = new Dictionary<string, string> { ["K"] = "V" } }, m => m.Env!["K"], "V");
        yield return Row("AuthRef", Physical() with { AuthRef = "secret://env/TOK" }, m => m.AuthRef, "secret://env/TOK");
        yield return Row("Tools", Physical() with { Tools = new[] { "search" } }, m => m.Tools!.Single(), "search");
        yield return Row("McpGatewayRef", Physical() with { McpGatewayRef = "demo-gw" }, m => m.McpGatewayRef, "demo-gw");
        yield return Row("OntologyRef", Physical() with { OntologyRef = "k8s-tools-v1" }, m => m.OntologyRef, "k8s-tools-v1");
        yield return Row("FailureOntologyRef", Physical() with { FailureOntologyRef = "my-failure-ref" }, m => m.FailureOntologyRef, "my-failure-ref");

        yield return Row("Virtual", Virtual(), m => m.Virtual, (object?)true);
        yield return Row("Sources.Ref", Virtual(), m => m.Sources!.Single().Ref, "up");
        yield return Row("ToolProjection.Name", VirtualProj(), m => m.ToolProjection!.Single().Name, "vname");
        yield return Row("ToolProjection.From", VirtualProj(), m => m.ToolProjection!.Single().From, "up");
        yield return Row("ToolProjection.SourceToolName", VirtualProj(), m => m.ToolProjection!.Single().SourceToolName, "orig");

        yield return Row("Container.Image", Container(ImageSpec()), m => m.Container!.Image, "img:1");
        yield return Row("Container.Build.Context", BuildContainer(new ContainerMcpBuildSpec { Context = "./ctx" }), m => m.Container!.Build!.Context, "./ctx");
        yield return Row("Container.Build.Dockerfile", BuildContainer(new ContainerMcpBuildSpec { Context = "./ctx", Dockerfile = "Dockerfile.custom" }), m => m.Container!.Build!.Dockerfile, "Dockerfile.custom");
        yield return Row("Container.Build.Args", BuildContainer(new ContainerMcpBuildSpec { Context = "./ctx", Args = new Dictionary<string, string> { ["V"] = "1" } }), m => m.Container!.Build!.Args!["V"], "1");
        yield return Row("Container.Build.Push", BuildContainer(new ContainerMcpBuildSpec { Context = "./ctx", Push = true }), m => m.Container!.Build!.Push, (object?)true);
        yield return Row("Container.Port", Container(ImageSpec() with { Port = 7100 }), m => m.Container!.Port, (object?)7100);
        yield return Row("Container.Path", Container(ImageSpec() with { Path = "/custom" }), m => m.Container!.Path, "/custom");
        yield return Row("Container.HealthPath", Container(ImageSpec() with { HealthPath = "/ready" }), m => m.Container!.HealthPath, "/ready");
        yield return Row("Container.Command", Container(ImageSpec() with { Command = new[] { "python", "b.py" } }), m => m.Container!.Command![0], "python");
        yield return Row("Container.Args", Container(ImageSpec() with { Args = new[] { "--v" } }), m => m.Container!.Args!.Single(), "--v");
        yield return Row("Container.Env", Container(ImageSpec() with { Env = new Dictionary<string, string> { ["FOO"] = "bar" } }), m => m.Container!.Env!["FOO"], "bar");
        yield return Row("Container.Secrets", Container(ImageSpec() with { Secrets = new Dictionary<string, string> { ["TOKEN"] = "secret://x" } }), m => m.Container!.Secrets!["TOKEN"], "secret://x");
        yield return Row("Container.StartupTimeoutSeconds", Container(ImageSpec() with { StartupTimeoutSeconds = 60 }), m => m.Container!.StartupTimeoutSeconds, (object?)60);
        yield return Row("Container.ImagePullPolicy", Container(ImageSpec() with { ImagePullPolicy = "Always" }), m => m.Container!.ImagePullPolicy, "Always");
        yield return Row("Container.Resources.Memory", Container(ImageSpec() with { Resources = new ContainerMcpResources { Memory = "256Mi" } }), m => m.Container!.Resources!.Memory, "256Mi");
        yield return Row("Container.Resources.Cpu", Container(ImageSpec() with { Resources = new ContainerMcpResources { Cpu = "0.5" } }), m => m.Container!.Resources!.Cpu, "0.5");
        yield return Row("Container.Resources.PidsLimit", Container(ImageSpec() with { Resources = new ContainerMcpResources { PidsLimit = 100 } }), m => m.Container!.Resources!.PidsLimit, (object?)100L);
        yield return Row("Container.Kubernetes.ServiceUrl", Container(ImageSpec() with { Kubernetes = new ContainerMcpKubernetesConfig { ServiceUrl = "http://svc:7000" } }), m => m.Container!.Kubernetes!.ServiceUrl, "http://svc:7000");
        yield return Row("Container.Kubernetes.DeploymentName", Container(ImageSpec() with { Kubernetes = new ContainerMcpKubernetesConfig { ServiceUrl = "http://svc:7000", DeploymentName = "dep" } }), m => m.Container!.Kubernetes!.DeploymentName, "dep");
        yield return Row("Container.Kubernetes.Namespace", Container(ImageSpec() with { Kubernetes = new ContainerMcpKubernetesConfig { ServiceUrl = "http://svc:7000", Namespace = "tools" } }), m => m.Container!.Kubernetes!.Namespace, "tools");
    }

    [Theory]
    [MemberData(nameof(RoundTripCases), DisableDiscoveryEnumeration = true)]
    public async Task Field_RoundTrips(string path, McpServerManifest input, Func<McpServerManifest, object?> extract, object? expected)
    {
        var json = EnvelopeSerializer.Serialize(input);
        var resources = await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(json);
        var server = ((ManifestResource.McpServerCase)resources.Single()).Server;
        extract(server).Should().Be(expected,
            because: $"{path} must survive the McpServer EnvelopeSerializer → JsonAgentGraphManifestLoader round-trip");
    }

    [Fact]
    public void AllMcpServerFields_AreCovered()
    {
        var covered = new HashSet<string>(RoundTripCases().Select(r => (string)r[0]!), StringComparer.Ordinal);
        var discovered = ManifestRoundTripWalker.Discover(typeof(McpServerManifest), AlwaysSerialized);
        discovered.Distinct().Except(covered).OrderBy(p => p).Should().BeEmpty(
            because: "every optional field on McpServerManifest must have a round-trip case in RoundTripCases()");
    }

    private static readonly IReadOnlySet<string> AlwaysSerialized = new HashSet<string>(StringComparer.Ordinal)
    {
        "Id", "Version",
    };
}
