// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Unit tests for <see cref="KubernetesContainerSupervisor"/>.
/// </summary>
public sealed class KubernetesContainerSupervisorTests
{
    private static ContainerPluginDescriptor MakeDescriptor(string? serviceUrl = "http://localhost:8080") =>
        new()
        {
            Name = "test-plugin",
            Image = "my-registry/test-plugin:1.0",
            InvokeBaseUrl = serviceUrl ?? "",
            KubernetesConfig = serviceUrl is not null
                ? new KubernetesPluginConfig(serviceUrl, "test-plugin", "default")
                : null,
        };

    [Fact]
    public async Task DrainAndReplaceAsync_NoKubernetesConfig_ReturnsStartFailed()
    {
        var descriptor = new ContainerPluginDescriptor
        {
            Name = "test",
            Image = "image:1.0",
            InvokeBaseUrl = "http://localhost:8080",
            KubernetesConfig = null,
        };
        var k8s = Substitute.For<IKubernetes>();
        var supervisor = new KubernetesContainerSupervisor(descriptor, k8s, NullLogger.Instance);

        var result = await supervisor.DrainAndReplaceAsync("image:2.0", CancellationToken.None);

        result.Outcome.Should().Be(ContainerReplaceOutcome.StartFailed);
        result.ErrorDetail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DrainAndReplaceAsync_PatchSucceeds_ReturnsRolloutStarted()
    {
        var descriptor = MakeDescriptor();
        var k8s = Substitute.For<IKubernetes>();
        var appsV1 = Substitute.For<IAppsV1Operations>();
        k8s.AppsV1.Returns(appsV1);
        appsV1.PatchNamespacedDeploymentWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1Deployment>
            {
                Body = new V1Deployment(),
            }));

        var supervisor = new KubernetesContainerSupervisor(descriptor, k8s, NullLogger.Instance);

        var result = await supervisor.DrainAndReplaceAsync("image:2.0", CancellationToken.None);

        result.Outcome.Should().Be(ContainerReplaceOutcome.RolloutStarted);
    }

    [Fact]
    public async Task DrainAndReplaceAsync_PatchThrows_ReturnsStartFailed()
    {
        var descriptor = MakeDescriptor();
        var k8s = Substitute.For<IKubernetes>();
        var appsV1 = Substitute.For<IAppsV1Operations>();
        k8s.AppsV1.Returns(appsV1);
        appsV1.PatchNamespacedDeploymentWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpOperationException("Deployment not found"));

        var supervisor = new KubernetesContainerSupervisor(descriptor, k8s, NullLogger.Instance);

        var result = await supervisor.DrainAndReplaceAsync("image:2.0", CancellationToken.None);

        result.Outcome.Should().Be(ContainerReplaceOutcome.StartFailed);
        result.ErrorDetail.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DrainAndReplaceAsync_UpdatesDescriptorImageWhenNewImageProvided()
    {
        var descriptor = MakeDescriptor();
        var k8s = Substitute.For<IKubernetes>();
        var appsV1 = Substitute.For<IAppsV1Operations>();
        k8s.AppsV1.Returns(appsV1);
        appsV1.PatchNamespacedDeploymentWithHttpMessagesAsync(
                Arg.Any<V1Patch>(), Arg.Any<string>(), Arg.Any<string>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HttpOperationResponse<V1Deployment> { Body = new V1Deployment() }));

        var supervisor = new KubernetesContainerSupervisor(descriptor, k8s, NullLogger.Instance);
        await supervisor.DrainAndReplaceAsync("image:2.0", CancellationToken.None);

        descriptor.Image.Should().Be("image:2.0");
    }

    [Fact]
    public void TryAcquireInvoke_WhenReady_ReturnsTrue()
    {
        var descriptor = MakeDescriptor();
        var k8s = Substitute.For<IKubernetes>();
        var supervisor = new KubernetesContainerSupervisor(descriptor, k8s, NullLogger.Instance);
        // Manually set status to Ready via StartAsync stubbing is complex;
        // use reflection to set the backing field.
        typeof(KubernetesContainerSupervisor)
            .GetField("_status", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(supervisor, ContainerPluginStatus.Ready);

        supervisor.TryAcquireInvoke().Should().BeTrue();
    }

    [Fact]
    public void TryAcquireInvoke_WhenNotReady_ReturnsFalse()
    {
        var descriptor = MakeDescriptor();
        var k8s = Substitute.For<IKubernetes>();
        var supervisor = new KubernetesContainerSupervisor(descriptor, k8s, NullLogger.Instance);
        // Default status is Created — not Ready

        supervisor.TryAcquireInvoke().Should().BeFalse();
    }
}
