// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Spectre.Console.Testing;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// MS-1c guard — <c>vais get -o yaml/json</c> output must be a valid, re-appliable
/// envelope. Exercises <see cref="OutputFormatter"/>'s envelope rendering and feeds the
/// result back through the loaders, proving the apply → get → apply round-trip (P11) and
/// that scalar types survive the JSON → YAML conversion (numbers/bools stay typed).
/// </summary>
public sealed class GetReappliabilityTests
{
    private static TestConsole WideConsole()
    {
        var console = new TestConsole();
        console.Profile.Width = 10_000; // avoid line-wrapping machine output
        return console;
    }

    [Fact]
    public async Task ManifestEnvelope_Yaml_IsReappliable_PreservingScalarTypes()
    {
        var manifest = new McpServerManifest("mcp-x", "1.0")
        {
            Transport = "containerStdio",
            Container = new ContainerMcpSpec { Image = "img:1", Port = 7100, StartupTimeoutSeconds = 45 },
        };

        var console = WideConsole();
        OutputFormatter.WriteManifestEnvelope(manifest, "McpServer", OutputFormat.Yaml, console);

        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(console.Output);
        var server = ((ManifestResource.McpServerCase)resources.Single()).Server;
        server.Transport.Should().Be("containerStdio");
        server.Container!.Port.Should().Be(7100);             // int preserved, not "7100"
        server.Container.StartupTimeoutSeconds.Should().Be(45);
    }

    [Fact]
    public async Task ManifestEnvelope_Json_IsReappliable()
    {
        var manifest = new LlmGatewayConfigManifest("llm-gw", "1.0",
            new[] { new GatewayMiddlewareSpec("logging") })
        {
            RateLimit = new LlmRateLimitSpec { RequestsPerMinute = 100 },
        };

        var console = WideConsole();
        OutputFormatter.WriteManifestEnvelope(manifest, "LlmGatewayConfig", OutputFormat.Json, console);

        var resources = await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(console.Output);
        var cfg = ((ManifestResource.LlmGatewayConfigCase)resources.Single()).Config;
        cfg.RateLimit!.RequestsPerMinute.Should().Be(100);
        cfg.Middleware.Single().Name.Should().Be("logging");
    }

    [Fact]
    public async Task ManifestEnvelopeList_Yaml_IsReappliable()
    {
        var manifests = new[]
        {
            new McpServerManifest("a", "1.0") { Transport = "streamableHttp", Url = "http://x/mcp" },
            new McpServerManifest("b", "1.0") { Transport = "streamableHttp", Url = "http://y/mcp" },
        };

        var console = WideConsole();
        OutputFormatter.WriteManifestEnvelopeList(manifests, "McpServer", OutputFormat.Yaml, console);

        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(console.Output);
        resources.Should().HaveCount(2);
        resources.OfType<ManifestResource.McpServerCase>().Select(r => r.Server.Id)
            .Should().BeEquivalentTo(new[] { "a", "b" });
    }
}
