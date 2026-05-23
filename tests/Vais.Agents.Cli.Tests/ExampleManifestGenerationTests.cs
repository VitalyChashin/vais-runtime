// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Spectre.Console.Testing;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// MS-3-B guard — canonical example manifests under <c>contracts/examples/</c> are
/// generated from representative records via the same <c>vais get -o yaml</c> rendering
/// path (so examples match what users see), are kept in sync (run with
/// <c>VAIS_UPDATE_EXAMPLES=1</c> to regenerate), and are re-appliable: each loads cleanly
/// through <see cref="YamlAgentGraphManifestLoader"/>.
/// </summary>
public sealed class ExampleManifestGenerationTests
{
    [Fact] public Task Agent() => GenerateAndCheck(BuildAgent(), "Agent");
    [Fact] public Task AgentGraph() => GenerateAndCheck(BuildGraph(), "AgentGraph");
    [Fact] public Task McpServer() => GenerateAndCheck(BuildMcpServer(), "McpServer");
    [Fact] public Task LlmGatewayConfig() => GenerateAndCheck(BuildLlmGateway(), "LlmGatewayConfig");
    [Fact] public Task McpGatewayConfig() => GenerateAndCheck(BuildMcpGateway(), "McpGatewayConfig");
    [Fact] public Task ContainerPlugin() => GenerateAndCheck(BuildContainerPlugin(), "ContainerPlugin");
    [Fact] public Task EvalSuite() => GenerateAndCheck(BuildEvalSuite(), "EvalSuite");

    private static async Task GenerateAndCheck<T>(T instance, string kind) where T : notnull
    {
        var console = new TestConsole();
        console.Profile.Width = 10_000; // avoid wrapping machine output
        OutputFormatter.WriteManifestEnvelope(instance, kind, OutputFormat.Yaml, console);
        var yaml = Normalize(console.Output);

        // Re-appliability gate: the generated example must load cleanly.
        var resources = await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(yaml);
        resources.Should().ContainSingle($"the generated {kind} example must be a single, loadable manifest");

        var path = ExamplePath(kind);
        if (Environment.GetEnvironmentVariable("VAIS_UPDATE_EXAMPLES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, yaml);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"the {kind} example must be checked in at contracts/examples/ (run with VAIS_UPDATE_EXAMPLES=1 to regenerate)");
        Normalize(File.ReadAllText(path)).Should().Be(yaml,
            because: $"the checked-in {kind} example must match the generator (run with VAIS_UPDATE_EXAMPLES=1 to regenerate)");
    }

    // ── representative instances (concise + idiomatic, not kitchen-sink) ─────────

    private static AgentManifest BuildAgent() => new(
        "example-agent", "1.0",
        new AgentHandlerRef("declarative"),
        Array.Empty<ProtocolBinding>(),
        Array.Empty<ToolRef>())
    {
        Description = "Example declarative agent.",
        Model = new ModelSpec("openai", "gpt-4.1"),
        SystemPrompt = new SystemPromptSpec(Inline: "You are a helpful assistant."),
    };

    private static AgentGraphManifest BuildGraph() => new(
        "example-graph", "1.0", Entry: "start",
        Nodes: new[]
        {
            new GraphNode("start", "Agent", Ref: new GraphAgentRef("example-agent")),
            new GraphNode("done", "End"),
        },
        Edges: new[] { new GraphEdge("start", "done") },
        Description: "Example two-node graph.");

    private static McpServerManifest BuildMcpServer() => new("example-mcp", "1.0", Description: "Example MCP server.")
    {
        Transport = "streamableHttp",
        Url = "http://localhost:3000/mcp",
    };

    private static LlmGatewayConfigManifest BuildLlmGateway() => new(
        "example-llm-gw", "1.0",
        new[] { new GatewayMiddlewareSpec("logging") },
        Description: "Example LLM gateway config.")
    {
        RateLimit = new LlmRateLimitSpec { RequestsPerMinute = 120 },
    };

    private static McpGatewayConfigManifest BuildMcpGateway() => new(
        "example-mcp-gw", "1.0",
        new[] { new GatewayMiddlewareSpec("audit") },
        Description: "Example MCP gateway config.")
    {
        WorkspacePolicies = new Dictionary<string, McpWorkspacePolicySpec>
        {
            ["default"] = new McpWorkspacePolicySpec(AllowedTools: new[] { "search" }),
        },
    };

    private static ContainerPluginManifest BuildContainerPlugin() => new("example-plugin", "1.0", Description: "Example container plugin.")
    {
        Spec = new ContainerPluginSpec { Image = "ghcr.io/example/plugin:1.0", Port = 8080 },
    };

    private static EvalSuiteManifest BuildEvalSuite() => new("example-suite", "1.0", Description: "Example eval suite.")
    {
        Spec = new EvalSuiteSpec
        {
            AgentId = "example-agent",
            Cases = new[]
            {
                new EvalCase
                {
                    Id = "greets",
                    Input = "Say hello.",
                    Assertions = new[] { new EvalAssertion("contains", JsonDocument.Parse("{\"value\":\"hello\"}").RootElement.Clone()) },
                },
            },
        },
    };

    private static string Normalize(string s) => s.Replace("\r\n", "\n");

    private static string ExamplePath(string kind) => Path.Combine(AgenticContractsDir(), "examples", $"{kind}.example.yaml");

    private static string AgenticContractsDir() => RepoContracts.Dir();
}
