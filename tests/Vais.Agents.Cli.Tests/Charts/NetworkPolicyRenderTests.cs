// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests.Charts;

/// <summary>
/// Hermetic tests for the vais-plugin NetworkPolicy Helm template.
/// Uses <see cref="EmbeddedChartExtractor.ExtractToTemp"/> to read the embedded chart
/// without requiring a helm binary — validates template structure, not rendered output.
/// </summary>
public sealed class NetworkPolicyRenderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _templateContent;

    public NetworkPolicyRenderTests()
    {
        _tempDir = EmbeddedChartExtractor.ExtractToTemp();
        var templatePath = Path.Combine(_tempDir, "templates", "networkpolicy.yaml");
        _templateContent = File.ReadAllText(templatePath);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void Template_ExistsInExtractedChart()
    {
        var path = Path.Combine(_tempDir, "templates", "networkpolicy.yaml");
        File.Exists(path).Should().BeTrue("networkpolicy.yaml must be present in the chart templates");
    }

    [Fact]
    public void Template_IsGuardedByNetworkPolicyEnabledFlag()
    {
        _templateContent.Should().Contain("{{- if .Values.networkPolicy.enabled }}");
        _templateContent.Should().Contain("{{- end }}");
    }

    [Fact]
    public void Template_SetsKindNetworkPolicy()
    {
        _templateContent.Should().Contain("kind: NetworkPolicy");
        _templateContent.Should().Contain("apiVersion: networking.k8s.io/v1");
    }

    [Fact]
    public void Template_ReferencesPluginNameForSelectorAndMetadata()
    {
        _templateContent.Should().Contain(".Values.pluginName");
    }

    [Fact]
    public void Template_AllowsIngressFromRuntimeNamespaceAndPod()
    {
        _templateContent.Should().Contain(".Values.networkPolicy.runtimeService.namespace");
        _templateContent.Should().Contain(".Values.networkPolicy.runtimeService.name");
        _templateContent.Should().Contain(".Values.pluginPort");
    }

    [Fact]
    public void Template_AllowsEgressToDnsOnPorts53()
    {
        _templateContent.Should().Contain("k8s-app: kube-dns");
        _templateContent.Should().Contain("kube-system");
        _templateContent.Should().Contain("port: 53");
    }

    [Fact]
    public void Template_AllowsEgressToRuntimeServicePort()
    {
        _templateContent.Should().Contain(".Values.networkPolicy.runtimeService.port");
    }

    [Fact]
    public void Template_SupportsExtraEgressExtensionPoint()
    {
        _templateContent.Should().Contain(".Values.networkPolicy.extraEgress");
        _templateContent.Should().Contain("toYaml .Values.networkPolicy.extraEgress");
    }

    [Fact]
    public void ValuesYaml_ContainsNetworkPolicyBlock()
    {
        var valuesPath = Path.Combine(_tempDir, "values.yaml");
        var values = File.ReadAllText(valuesPath);
        values.Should().Contain("networkPolicy:");
        values.Should().Contain("enabled: true");
        values.Should().Contain("runtimeService:");
        values.Should().Contain("extraEgress: []");
    }
}
