// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// <see cref="IContainerSupervisor"/> for Kubernetes standalone topology.
/// Does not manage container lifecycle directly — delegates image rollouts via
/// <c>PATCH apps/v1/namespaces/{ns}/deployments/{name}</c> and returns
/// <see cref="ContainerReplaceOutcome.RolloutStarted"/> immediately.
/// </summary>
internal sealed class KubernetesContainerSupervisor : IContainerSupervisor
{
    private readonly ContainerPluginDescriptor _descriptor;
    private readonly IKubernetes _k8s;
    private readonly HttpClient _healthClient;
    private readonly ILogger _logger;

    private ContainerPluginStatus _status = ContainerPluginStatus.Created;

    public ContainerPluginDescriptor Descriptor => _descriptor;
    public ContainerPluginStatus Status => _status;

    internal KubernetesContainerSupervisor(
        ContainerPluginDescriptor descriptor,
        IKubernetes k8s,
        ILogger logger)
    {
        _descriptor = descriptor;
        _k8s = k8s;
        _healthClient = new HttpClient { BaseAddress = new Uri(descriptor.InvokeBaseUrl) };
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _status = ContainerPluginStatus.Starting;
        _logger.LogInformation(
            "Registering kubernetes container plugin '{Name}' at service '{ServiceUrl}'",
            _descriptor.Name, _descriptor.InvokeBaseUrl);

        try
        {
            await WaitForHealthAsync(ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Kubernetes plugin '{Name}' service not reachable at startup — marking ready anyway (K8s manages lifecycle)",
                _descriptor.Name);
        }

        _status = ContainerPluginStatus.Ready;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _status = ContainerPluginStatus.Stopped;
        return Task.CompletedTask;
    }

    public async Task<ContainerReplaceResult> DrainAndReplaceAsync(string? newImage, CancellationToken ct)
    {
        var k8sConfig = _descriptor.KubernetesConfig;
        if (k8sConfig is null)
            return new ContainerReplaceResult(ContainerReplaceOutcome.StartFailed, "No Kubernetes config on descriptor.");

        if (newImage is not null)
            _descriptor.Image = newImage;

        var mergePatch =
            $"{{\"spec\":{{\"template\":{{\"spec\":{{\"containers\":[{{\"name\":\"{k8sConfig.DeploymentName}\",\"image\":\"{_descriptor.Image}\"}}]}}}}}}}}";
        var patch = new V1Patch(mergePatch, V1Patch.PatchType.MergePatch);

        try
        {
            using var _ = await _k8s.AppsV1.PatchNamespacedDeploymentWithHttpMessagesAsync(
                patch, k8sConfig.DeploymentName, k8sConfig.Namespace,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to patch Kubernetes deployment '{DeploymentName}' in namespace '{Namespace}'",
                k8sConfig.DeploymentName, k8sConfig.Namespace);
            return new ContainerReplaceResult(ContainerReplaceOutcome.StartFailed, ex.Message);
        }

        _logger.LogInformation(
            "Kubernetes deployment '{DeploymentName}' in namespace '{Namespace}' patched — rollout started",
            k8sConfig.DeploymentName, k8sConfig.Namespace);
        return new ContainerReplaceResult(ContainerReplaceOutcome.RolloutStarted);
    }

    public bool TryAcquireInvoke() => _status == ContainerPluginStatus.Ready;

    public void ReleaseInvoke() { }

    public async Task WaitForHealthAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_descriptor.StartupTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await _healthClient.GetAsync("/health", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { throw; }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"[{ContainerPluginUrns.StartupTimeout}] Kubernetes plugin '{_descriptor.Name}' " +
            $"service not reachable within {_descriptor.StartupTimeoutSeconds}s at {_descriptor.InvokeBaseUrl}.");
    }

    public ValueTask DisposeAsync()
    {
        _healthClient.Dispose();
        _k8s.Dispose();
        return ValueTask.CompletedTask;
    }
}
