// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Tests for the Kubernetes YAML section parsing added in K6-2.
/// </summary>
public sealed class ContainerPluginYamlK8sTests
{
    private static readonly ContainerPluginYamlDeserializer _deserializer = new();

    [Fact]
    public void Deserialize_WithKubernetesSection_PopulatesKubernetesSpec()
    {
        var yaml = """
            apiVersion: v0.24
            kind: ContainerPlugin
            metadata:
              name: my-k8s-plugin
            spec:
              runtime: container
              image: my-registry/my-plugin:1.0
              kubernetes:
                serviceUrl: http://my-plugin.default.svc.cluster.local:8080
                deploymentName: my-plugin
                namespace: production
            """;

        var doc = _deserializer.Deserialize(yaml);

        doc.Should().NotBeNull();
        var k8s = doc!.Spec!.Kubernetes;
        k8s.Should().NotBeNull();
        k8s!.ServiceUrl.Should().Be("http://my-plugin.default.svc.cluster.local:8080");
        k8s.DeploymentName.Should().Be("my-plugin");
        k8s.Namespace.Should().Be("production");
    }

    [Fact]
    public void Deserialize_KubernetesNamespaceOmitted_DefaultsToDefault()
    {
        var yaml = """
            apiVersion: v0.24
            kind: ContainerPlugin
            metadata:
              name: my-plugin
            spec:
              runtime: container
              image: my-image:1.0
              kubernetes:
                serviceUrl: http://my-plugin:8080
                deploymentName: my-plugin
            """;

        var doc = _deserializer.Deserialize(yaml);

        doc!.Spec!.Kubernetes!.Namespace.Should().Be("default");
    }

    [Fact]
    public void Deserialize_WithoutKubernetesSection_KubernetesIsNull()
    {
        var yaml = """
            apiVersion: v0.24
            kind: ContainerPlugin
            metadata:
              name: my-plugin
            spec:
              runtime: container
              image: my-image:1.0
            """;

        var doc = _deserializer.Deserialize(yaml);

        doc!.Spec!.Kubernetes.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WithDurability_ParsesDurabilityField()
    {
        var yaml = """
            apiVersion: v0.24
            kind: ContainerPlugin
            metadata:
              name: my-plugin
            spec:
              runtime: container
              image: my-image:1.0
              durability: grain
            """;

        var doc = _deserializer.Deserialize(yaml);

        doc!.Spec!.Durability.Should().Be("grain");
    }
}
