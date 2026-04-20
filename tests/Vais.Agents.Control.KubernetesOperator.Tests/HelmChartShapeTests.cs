// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

/// <summary>
/// Smoke check on the shipped Helm chart + standalone CRD YAML. Guards
/// against accidental file deletion / renamed keys drifting from the
/// Deployment template's env-var names + RBAC verbs. Doesn't parse YAML
/// (no test-only dep); instead scans for required substrings.
/// </summary>
public sealed class HelmChartShapeTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void Chart_Yaml_Exists_AndCarriesAppVersion()
    {
        var path = Path.Combine(RepoRoot, "deploy", "helm", "vais-agents-operator", "Chart.yaml");
        File.Exists(path).Should().BeTrue($"chart file {path} should ship with the repo");
        var content = File.ReadAllText(path);
        content.Should().Contain("name: vais-agents-operator");
        content.Should().Contain("appVersion: \"0.13.0-preview\"");
    }

    [Fact]
    public void Deployment_Template_DeclaresExpectedEnvVars()
    {
        var path = Path.Combine(RepoRoot, "deploy", "helm", "vais-agents-operator", "templates", "deployment.yaml");
        File.Exists(path).Should().BeTrue();
        var content = File.ReadAllText(path);

        // Env-var key drift guard — these exact names match KubernetesOperatorOptions binding.
        content.Should().Contain("Vais__KubernetesOperator__ControlPlaneBaseUrl");
        content.Should().Contain("Vais__KubernetesOperator__ControlPlaneAudience");
        content.Should().Contain("Vais__KubernetesOperator__TokenPath");
        content.Should().Contain("Vais__KubernetesOperator__AuthMode");

        // Projected volume + health probes drift guard.
        content.Should().Contain("serviceAccountToken:");
        content.Should().Contain("path: /healthz");
        content.Should().Contain("path: /readyz");
    }

    [Fact]
    public void ClusterRole_Template_GrantsRequiredVerbs()
    {
        var path = Path.Combine(RepoRoot, "deploy", "helm", "vais-agents-operator", "templates", "clusterrole.yaml");
        File.Exists(path).Should().BeTrue();
        var content = File.ReadAllText(path);

        content.Should().Contain("vais.io");
        content.Should().Contain("agents");
        content.Should().Contain("agents/status");
        content.Should().Contain("agents/finalizers");
        content.Should().Contain("secrets");
        content.Should().Contain("events");
    }

    [Fact]
    public void StandaloneCrd_Yaml_RegistersAgentKind()
    {
        var path = Path.Combine(RepoRoot, "deploy", "crds", "vais.io_agents.yaml");
        File.Exists(path).Should().BeTrue();
        var content = File.ReadAllText(path);

        content.Should().Contain("kind: CustomResourceDefinition");
        content.Should().Contain("name: agents.vais.io");
        content.Should().Contain("group: vais.io");
        content.Should().Contain("scope: Namespaced");
        content.Should().Contain("kind: Agent");
        content.Should().Contain("plural: agents");
        content.Should().Contain("- vagent");
        content.Should().Contain("- vagents");
        content.Should().Contain("x-kubernetes-preserve-unknown-fields: true");
    }

    [Fact]
    public void ValuesYaml_HasControlPlaneBaseUrlPlaceholder()
    {
        var path = Path.Combine(RepoRoot, "deploy", "helm", "vais-agents-operator", "values.yaml");
        File.Exists(path).Should().BeTrue();
        var content = File.ReadAllText(path);
        content.Should().Contain("controlPlane:");
        content.Should().Contain("baseUrl:");
    }

    private static string FindRepoRoot()
    {
        // Walk up from the test-binary location until we find Vais.Agents.sln.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vais.Agents.sln")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate Vais.Agents.sln walking up from test output dir.");
        }
        return dir.FullName;
    }
}
