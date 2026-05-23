// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Round-trip completeness guard for <see cref="ContainerPluginManifest"/> — every
/// optional field (incl. the nested <c>ContainerPluginSpec</c> → build / retry / kubernetes
/// tree) must survive <c>EnvelopeSerializer.Serialize</c> → <see cref="JsonAgentGraphManifestLoader"/>.
/// </summary>
public sealed class ContainerPluginManifestFieldRoundTripTests
{
    private static ContainerPluginManifest Rich() => new(
        "plugin-x", "1.0",
        Description: "a plugin",
        Labels: new Dictionary<string, string> { ["team"] = "platform" })
    {
        Spec = new ContainerPluginSpec
        {
            Image = "reg/plugin:1.0",
            Build = new ContainerPluginBuildSpec
            {
                Context = "./ctx",
                Dockerfile = "Dockerfile.custom",
                Args = new Dictionary<string, string> { ["V"] = "1" },
                Push = true,
            },
            Port = 9090,
            Topology = "kubernetes",
            StartupTimeoutSeconds = 45,
            InvokeTimeoutSeconds = 120,
            SessionTtlSeconds = 1800,
            InvokeIdleTimeoutSeconds = 90,
            ImagePullPolicy = "Always",
            RetryPolicy = new ContainerPluginRetryPolicy(5, 3, new[] { "timeout" }),
            Kubernetes = new ContainerPluginKubernetesConfig
            {
                ServiceUrl = "http://svc:9090",
                DeploymentName = "dep",
                Namespace = "plugins",
            },
            Secrets = new Dictionary<string, string> { ["TOK"] = "secret://x" },
        },
    };

    private static object?[] Row(string path, Func<ContainerPluginManifest, object?> extract, object? expected)
        => [path, extract, expected];

    public static IEnumerable<object?[]> RoundTripCases()
    {
        yield return Row("Description", m => m.Description, "a plugin");
        yield return Row("Labels", m => m.Labels!["team"], "platform");
        yield return Row("Spec.Image", m => m.Spec.Image, "reg/plugin:1.0");
        yield return Row("Spec.Build.Context", m => m.Spec.Build!.Context, "./ctx");
        yield return Row("Spec.Build.Dockerfile", m => m.Spec.Build!.Dockerfile, "Dockerfile.custom");
        yield return Row("Spec.Build.Args", m => m.Spec.Build!.Args!["V"], "1");
        yield return Row("Spec.Build.Push", m => m.Spec.Build!.Push, (object?)true);
        yield return Row("Spec.Port", m => m.Spec.Port, (object?)9090);
        yield return Row("Spec.Topology", m => m.Spec.Topology, "kubernetes");
        yield return Row("Spec.StartupTimeoutSeconds", m => m.Spec.StartupTimeoutSeconds, (object?)45);
        yield return Row("Spec.InvokeTimeoutSeconds", m => m.Spec.InvokeTimeoutSeconds, (object?)120);
        yield return Row("Spec.SessionTtlSeconds", m => m.Spec.SessionTtlSeconds, (object?)1800);
        yield return Row("Spec.InvokeIdleTimeoutSeconds", m => m.Spec.InvokeIdleTimeoutSeconds, (object?)90);
        yield return Row("Spec.ImagePullPolicy", m => m.Spec.ImagePullPolicy, "Always");
        yield return Row("Spec.RetryPolicy.MaxAttempts", m => m.Spec.RetryPolicy!.MaxAttempts, (object?)5);
        yield return Row("Spec.RetryPolicy.BackoffSeconds", m => m.Spec.RetryPolicy!.BackoffSeconds, (object?)3);
        yield return Row("Spec.RetryPolicy.RetryOn", m => m.Spec.RetryPolicy!.RetryOn.Single(), "timeout");
        yield return Row("Spec.Kubernetes.ServiceUrl", m => m.Spec.Kubernetes!.ServiceUrl, "http://svc:9090");
        yield return Row("Spec.Kubernetes.DeploymentName", m => m.Spec.Kubernetes!.DeploymentName, "dep");
        yield return Row("Spec.Kubernetes.Namespace", m => m.Spec.Kubernetes!.Namespace, "plugins");
        yield return Row("Spec.Secrets", m => m.Spec.Secrets!["TOK"], "secret://x");
    }

    [Theory]
    [MemberData(nameof(RoundTripCases), DisableDiscoveryEnumeration = true)]
    public async Task Field_RoundTrips(string path, Func<ContainerPluginManifest, object?> extract, object? expected)
    {
        var json = EnvelopeSerializer.Serialize(Rich());
        var resources = await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(json);
        var manifest = ((ManifestResource.ContainerPluginCase)resources.Single()).Manifest;
        extract(manifest).Should().Be(expected,
            because: $"{path} must survive the ContainerPlugin EnvelopeSerializer → JsonAgentGraphManifestLoader round-trip");
    }

    [Fact]
    public void AllContainerPluginFields_AreCovered()
    {
        var covered = new HashSet<string>(RoundTripCases().Select(r => (string)r[0]!), StringComparer.Ordinal);
        var discovered = ManifestRoundTripWalker.Discover(typeof(ContainerPluginManifest),
            new HashSet<string>(StringComparer.Ordinal) { "Id", "Version" });
        discovered.Distinct().Except(covered).OrderBy(p => p).Should().BeEmpty(
            because: "every optional field on ContainerPluginManifest must have a round-trip case");
    }
}
