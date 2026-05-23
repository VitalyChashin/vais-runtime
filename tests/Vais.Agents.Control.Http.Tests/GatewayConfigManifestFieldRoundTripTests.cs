// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Round-trip completeness guard for <see cref="LlmGatewayConfigManifest"/> — every
/// optional field (incl. nested <c>GatewayMiddlewareSpec</c> and <c>LlmRateLimitSpec</c>)
/// must survive <c>EnvelopeSerializer.Serialize</c> → <see cref="JsonAgentGraphManifestLoader"/>.
/// </summary>
public sealed class LlmGatewayConfigFieldRoundTripTests
{
    private static LlmGatewayConfigManifest Rich() => new(
        "llm-gw", "1.0",
        new[] { new GatewayMiddlewareSpec("logging", JsonDocument.Parse("{\"level\":\"info\"}").RootElement.Clone()) },
        Description: "llm gw",
        Labels: new Dictionary<string, string> { ["team"] = "platform" })
    {
        RateLimit = new LlmRateLimitSpec { RequestsPerMinute = 100, TokensPerMinute = 5000 },
        Annotations = new Dictionary<string, string> { ["owner"] = "vais" },
    };

    private static object?[] Row(string path, Func<LlmGatewayConfigManifest, object?> extract, object? expected)
        => [path, extract, expected];

    public static IEnumerable<object?[]> RoundTripCases()
    {
        yield return Row("Middleware.Name", m => m.Middleware.Single().Name, "logging");
        yield return Row("Middleware.Params", m => m.Middleware.Single().Params!.Value.GetProperty("level").GetString(), "info");
        yield return Row("Description", m => m.Description, "llm gw");
        yield return Row("Labels", m => m.Labels!["team"], "platform");
        yield return Row("RateLimit.RequestsPerMinute", m => m.RateLimit!.RequestsPerMinute, (object?)100);
        yield return Row("RateLimit.TokensPerMinute", m => m.RateLimit!.TokensPerMinute, (object?)5000);
        yield return Row("Annotations", m => m.Annotations!["owner"], "vais");
    }

    [Theory]
    [MemberData(nameof(RoundTripCases), DisableDiscoveryEnumeration = true)]
    public async Task Field_RoundTrips(string path, Func<LlmGatewayConfigManifest, object?> extract, object? expected)
    {
        var json = EnvelopeSerializer.Serialize(Rich());
        var resources = await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(json);
        var config = ((ManifestResource.LlmGatewayConfigCase)resources.Single()).Config;
        extract(config).Should().Be(expected,
            because: $"{path} must survive the LlmGatewayConfig EnvelopeSerializer → JsonAgentGraphManifestLoader round-trip");
    }

    [Fact]
    public void AllLlmGatewayConfigFields_AreCovered()
    {
        var covered = new HashSet<string>(RoundTripCases().Select(r => (string)r[0]!), StringComparer.Ordinal);
        var discovered = ManifestRoundTripWalker.Discover(typeof(LlmGatewayConfigManifest),
            new HashSet<string>(StringComparer.Ordinal) { "Id", "Version" });
        discovered.Distinct().Except(covered).OrderBy(p => p).Should().BeEmpty(
            because: "every optional field on LlmGatewayConfigManifest must have a round-trip case");
    }
}

/// <summary>
/// Round-trip completeness guard for <see cref="McpGatewayConfigManifest"/> — every
/// optional field (incl. nested <c>GatewayMiddlewareSpec</c> and <c>McpWorkspacePolicySpec</c>)
/// must survive <c>EnvelopeSerializer.Serialize</c> → <see cref="JsonAgentGraphManifestLoader"/>.
/// </summary>
public sealed class McpGatewayConfigFieldRoundTripTests
{
    private static McpGatewayConfigManifest Rich() => new(
        "mcp-gw", "1.0",
        new[] { new GatewayMiddlewareSpec("audit", JsonDocument.Parse("{\"mode\":\"strict\"}").RootElement.Clone()) },
        Description: "mcp gw",
        Labels: new Dictionary<string, string> { ["team"] = "platform" })
    {
        WorkspacePolicies = new Dictionary<string, McpWorkspacePolicySpec>
        {
            ["ws-1"] = new McpWorkspacePolicySpec(AllowedTools: new[] { "a" }, DeniedTools: new[] { "b" }, MinPrivilegeLevel: 3),
        },
        Annotations = new Dictionary<string, string> { ["owner"] = "vais" },
    };

    private static object?[] Row(string path, Func<McpGatewayConfigManifest, object?> extract, object? expected)
        => [path, extract, expected];

    public static IEnumerable<object?[]> RoundTripCases()
    {
        yield return Row("Middleware.Name", m => m.Middleware.Single().Name, "audit");
        yield return Row("Middleware.Params", m => m.Middleware.Single().Params!.Value.GetProperty("mode").GetString(), "strict");
        yield return Row("Description", m => m.Description, "mcp gw");
        yield return Row("Labels", m => m.Labels!["team"], "platform");
        yield return Row("WorkspacePolicies.AllowedTools", m => m.WorkspacePolicies!["ws-1"].AllowedTools!.Single(), "a");
        yield return Row("WorkspacePolicies.DeniedTools", m => m.WorkspacePolicies!["ws-1"].DeniedTools!.Single(), "b");
        yield return Row("WorkspacePolicies.MinPrivilegeLevel", m => m.WorkspacePolicies!["ws-1"].MinPrivilegeLevel, (object?)3);
        yield return Row("Annotations", m => m.Annotations!["owner"], "vais");
    }

    [Theory]
    [MemberData(nameof(RoundTripCases), DisableDiscoveryEnumeration = true)]
    public async Task Field_RoundTrips(string path, Func<McpGatewayConfigManifest, object?> extract, object? expected)
    {
        var json = EnvelopeSerializer.Serialize(Rich());
        var resources = await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(json);
        var config = ((ManifestResource.McpGatewayConfigCase)resources.Single()).Config;
        extract(config).Should().Be(expected,
            because: $"{path} must survive the McpGatewayConfig EnvelopeSerializer → JsonAgentGraphManifestLoader round-trip");
    }

    [Fact]
    public void AllMcpGatewayConfigFields_AreCovered()
    {
        var covered = new HashSet<string>(RoundTripCases().Select(r => (string)r[0]!), StringComparer.Ordinal);
        var discovered = ManifestRoundTripWalker.Discover(typeof(McpGatewayConfigManifest),
            new HashSet<string>(StringComparer.Ordinal) { "Id", "Version" });
        discovered.Distinct().Except(covered).OrderBy(p => p).Should().BeEmpty(
            because: "every optional field on McpGatewayConfigManifest must have a round-trip case");
    }
}
