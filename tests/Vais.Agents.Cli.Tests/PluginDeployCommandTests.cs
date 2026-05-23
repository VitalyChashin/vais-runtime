// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// Tests for <see cref="PluginDeployCommand"/> argument building.
/// </summary>
public sealed class PluginDeployCommandTests
{
    private static PluginDeployCommand.Settings DefaultSettings(
        string releaseName = "my-plugin",
        string image = "my-registry/my-plugin:1.2.3",
        string ns = "default",
        int replicas = 1,
        int port = 8080,
        string imagePullPolicy = "IfNotPresent",
        string? valuesFile = null,
        string[]? setValues = null,
        bool dryRun = false,
        int? workspaceSizeMb = null,
        string workspacePath = "/workspace",
        string workspaceMedium = "disk",
        bool workspacePersist = false,
        string? workspaceStorageClass = null) =>
        new()
        {
            ReleaseName = releaseName,
            Image = image,
            Namespace = ns,
            Replicas = replicas,
            Port = port,
            ImagePullPolicy = imagePullPolicy,
            ValuesFile = valuesFile,
            SetValues = setValues,
            DryRun = dryRun,
            WorkspaceSizeMb = workspaceSizeMb,
            WorkspacePath = workspacePath,
            WorkspaceMedium = workspaceMedium,
            WorkspacePersist = workspacePersist,
            WorkspaceStorageClass = workspaceStorageClass,
        };

    [Fact]
    public void BuildHelmArgs_ContainsReleaseNameAndChartDir()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(), "/tmp/chart");

        args.Should().Contain("upgrade --install my-plugin /tmp/chart");
    }

    [Fact]
    public void BuildHelmArgs_ContainsNamespace()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(ns: "production"), "/tmp/chart");

        args.Should().Contain("--namespace production");
    }

    [Fact]
    public void BuildHelmArgs_SetsImageRepositoryAndTag()
    {
        var args = PluginDeployCommand.BuildHelmArgs(
            DefaultSettings(image: "my-registry/my-plugin:1.2.3"), "/tmp/chart");

        args.Should().Contain("--set image.repository=my-registry/my-plugin");
        args.Should().Contain("--set image.tag=1.2.3");
    }

    [Fact]
    public void BuildHelmArgs_ImageWithoutTag_UsesLatest()
    {
        var args = PluginDeployCommand.BuildHelmArgs(
            DefaultSettings(image: "my-registry/my-plugin"), "/tmp/chart");

        args.Should().Contain("--set image.tag=latest");
    }

    [Fact]
    public void BuildHelmArgs_SetsReplicaCount()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(replicas: 3), "/tmp/chart");

        args.Should().Contain("--set replicaCount=3");
    }

    [Fact]
    public void BuildHelmArgs_SetsPluginPort()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(port: 9090), "/tmp/chart");

        args.Should().Contain("--set pluginPort=9090");
    }

    [Fact]
    public void BuildHelmArgs_WithValuesFile_IncludesFileFlag()
    {
        var args = PluginDeployCommand.BuildHelmArgs(
            DefaultSettings(valuesFile: "/path/to/values.yaml"), "/tmp/chart");

        args.Should().Contain("-f /path/to/values.yaml");
    }

    [Fact]
    public void BuildHelmArgs_WithSetValues_IncludesAllOverrides()
    {
        var args = PluginDeployCommand.BuildHelmArgs(
            DefaultSettings(setValues: ["resources.limits.cpu=1", "resources.limits.memory=256Mi"]),
            "/tmp/chart");

        args.Should().Contain("--set resources.limits.cpu=1");
        args.Should().Contain("--set resources.limits.memory=256Mi");
    }

    [Fact]
    public void BuildHelmArgs_WithDryRun_AppendsDryRunFlag()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(dryRun: true), "/tmp/chart");

        args.Should().Contain("--dry-run");
    }

    [Fact]
    public void BuildHelmArgs_WithoutDryRun_DoesNotContainDryRunFlag()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(dryRun: false), "/tmp/chart");

        args.Should().NotContain("--dry-run");
    }

    [Fact]
    public void BuildHelmArgs_IncludesCreateNamespace()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(), "/tmp/chart");

        args.Should().Contain("--create-namespace");
    }

    [Fact]
    public void BuildHelmArgs_DefaultImagePullPolicy_IsIfNotPresent()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(), "/tmp/chart");

        args.Should().Contain("--set image.pullPolicy=IfNotPresent");
    }

    [Fact]
    public void BuildHelmArgs_CustomImagePullPolicy_IsPassedThrough()
    {
        var args = PluginDeployCommand.BuildHelmArgs(
            DefaultSettings(imagePullPolicy: "Never"), "/tmp/chart");

        args.Should().Contain("--set image.pullPolicy=Never");
    }

    // ── Workspace ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildHelmArgs_NoWorkspaceSize_OmitsWorkspaceFlags()
    {
        var args = PluginDeployCommand.BuildHelmArgs(DefaultSettings(), "/tmp/chart");

        args.Should().NotContain("workspace.enabled");
    }

    [Fact]
    public void BuildHelmArgs_WithWorkspaceSize_EnablesAndSetsWorkspace()
    {
        var args = PluginDeployCommand.BuildHelmArgs(
            DefaultSettings(workspaceSizeMb: 4096, workspacePath: "/repo", workspaceMedium: "disk", workspacePersist: true),
            "/tmp/chart");

        args.Should().Contain("--set workspace.enabled=true");
        args.Should().Contain("--set workspace.path=/repo");
        args.Should().Contain("--set workspace.sizeMb=4096");
        args.Should().Contain("--set workspace.medium=disk");
        args.Should().Contain("--set workspace.persist=true");
    }

    [Fact]
    public void BuildHelmArgs_WithWorkspaceStorageClass_IncludesIt()
    {
        var args = PluginDeployCommand.BuildHelmArgs(
            DefaultSettings(workspaceSizeMb: 1024, workspacePersist: true, workspaceStorageClass: "fast-ssd"),
            "/tmp/chart");

        args.Should().Contain("--set workspace.storageClassName=fast-ssd");
    }

    [Fact]
    public void BuildManifest_NoWorkspaceSize_LeavesWorkspaceNull()
    {
        var manifest = PluginDeployCommand.BuildManifest(DefaultSettings());

        manifest.Spec.Workspace.Should().BeNull();
    }

    [Fact]
    public void BuildManifest_WithWorkspace_PopulatesSpecWorkspace()
    {
        var manifest = PluginDeployCommand.BuildManifest(
            DefaultSettings(workspaceSizeMb: 2048, workspacePath: "/repo", workspaceMedium: "memory", workspacePersist: false));

        manifest.Spec.Workspace.Should().NotBeNull();
        manifest.Spec.Workspace!.Path.Should().Be("/repo");
        manifest.Spec.Workspace.SizeMb.Should().Be(2048);
        manifest.Spec.Workspace.Medium.Should().Be("memory");
        manifest.Spec.Workspace.Persist.Should().BeFalse();
    }
}
