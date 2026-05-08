// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Manages the Docker container lifecycle for one container plugin.
/// States: Created → Starting → Ready → Stopping → Stopped / Failed.
/// </summary>
internal sealed class ContainerSupervisor : IAsyncDisposable
{
    private readonly ContainerPluginDescriptor _descriptor;
    private readonly IDockerClient _docker;
    private readonly HttpClient _healthClient;
    private readonly ILogger _logger;

    private ContainerPluginStatus _status = ContainerPluginStatus.Created;
    private string? _containerId;

    private int _activeInvokes;
    private bool _draining;
    private TaskCompletionSource? _drainSignal;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _replaceLock = new(1, 1);

    internal ContainerPluginDescriptor Descriptor => _descriptor;
    internal ContainerPluginStatus Status => _status;

    internal ContainerSupervisor(
        ContainerPluginDescriptor descriptor,
        IDockerClient docker,
        ILogger logger)
    {
        _descriptor = descriptor;
        _docker = docker;
        _healthClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{descriptor.Port}") };
        _logger = logger;
    }

    internal async Task StartAsync(CancellationToken ct = default)
    {
        _status = ContainerPluginStatus.Starting;
        _logger.LogInformation(
            "Starting container plugin '{Name}' from image '{Image}'", _descriptor.Name, _descriptor.Image);

        var envVars = new List<string>
        {
            $"VAIS_PLUGIN_PORT={_descriptor.Port}",
        };
        foreach (var kv in _descriptor.SecretRefs)
            envVars.Add($"{kv.Key}={kv.Value}");

        var exposedPorts = new Dictionary<string, EmptyStruct>
        {
            { $"{_descriptor.Port}/tcp", new EmptyStruct() }
        };

        var containerName = $"vais-plugin-{_descriptor.Name}";
        await RemoveExistingContainerAsync(containerName, ct).ConfigureAwait(false);

        var createResp = await _docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Name = containerName,
                Image = _descriptor.Image,
                Env = envVars,
                ExposedPorts = exposedPorts,
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        {
                            $"{_descriptor.Port}/tcp",
                            new List<PortBinding> { new() { HostPort = _descriptor.Port.ToString() } }
                        }
                    }
                }
            }, ct).ConfigureAwait(false);

        _containerId = createResp.ID;
        await _docker.Containers.StartContainerAsync(_containerId, null, ct).ConfigureAwait(false);

        await WaitForHealthAsync(ct).ConfigureAwait(false);
        _status = ContainerPluginStatus.Ready;
        _logger.LogInformation(
            "Container plugin '{Name}' is ready (containerId={ContainerId})", _descriptor.Name, _containerId);
    }

    internal async Task StopAsync(CancellationToken ct = default)
    {
        if (_containerId is null) return;
        _status = ContainerPluginStatus.Stopping;
        _logger.LogDebug("Stopping container plugin '{Name}' (containerId={ContainerId})", _descriptor.Name, _containerId);
        try
        {
            await _docker.Containers.StopContainerAsync(
                _containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
                ct).ConfigureAwait(false);
            await _docker.Containers.RemoveContainerAsync(
                _containerId,
                new ContainerRemoveParameters { Force = true },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping container plugin '{Name}'", _descriptor.Name);
        }
        _containerId = null;
        _status = ContainerPluginStatus.Stopped;
    }

    internal async Task<ContainerReplaceResult> DrainAndReplaceAsync(string? newImage, CancellationToken ct)
    {
        await _replaceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TaskCompletionSource? signal = null;
            lock (_stateLock)
            {
                _draining = true;
                if (_activeInvokes > 0)
                {
                    _drainSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    signal = _drainSignal;
                }
            }
            if (signal is not null)
                await signal.Task.WaitAsync(ct).ConfigureAwait(false);

            await StopAsync(ct).ConfigureAwait(false);

            if (newImage is not null)
                _descriptor.Image = newImage;

            try
            {
                await StartAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                return new ContainerReplaceResult(ContainerReplaceOutcome.HandshakeFailed, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ContainerReplaceResult(ContainerReplaceOutcome.StartFailed, ex.Message);
            }

            // Re-verify handler type name has not changed (ABI invariant).
            var prevTypeName = _descriptor.HandlerTypeName;
            if (!string.IsNullOrEmpty(prevTypeName))
            {
                try
                {
                    using var resp = await _healthClient.GetAsync("/v1/metadata", ct).ConfigureAwait(false);
                    if (resp.IsSuccessStatusCode)
                    {
                        var meta = await resp.Content
                            .ReadFromJsonAsync<PluginMetadataResponse>(ContainerJsonOptions.Default, ct)
                            .ConfigureAwait(false);
                        if (meta is not null && !string.IsNullOrEmpty(meta.HandlerTypeName)
                            && !string.Equals(meta.HandlerTypeName, prevTypeName, StringComparison.Ordinal))
                        {
                            // Restore old type name and fail — silo restart required.
                            _descriptor.HandlerTypeName = prevTypeName;
                            return new ContainerReplaceResult(
                                ContainerReplaceOutcome.HandlerTypeNameChanged,
                                $"HandlerTypeName changed from '{prevTypeName}' to '{meta.HandlerTypeName}'.");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not verify metadata after replace for '{Name}'", _descriptor.Name);
                }
            }

            lock (_stateLock)
            {
                _draining = false;
                _drainSignal = null;
            }
            return new ContainerReplaceResult(ContainerReplaceOutcome.Success);
        }
        finally
        {
            _replaceLock.Release();
        }
    }

    internal bool TryAcquireInvoke()
    {
        lock (_stateLock)
        {
            if (_draining || _status != ContainerPluginStatus.Ready) return false;
            _activeInvokes++;
            return true;
        }
    }

    internal void ReleaseInvoke()
    {
        TaskCompletionSource? signal = null;
        lock (_stateLock)
        {
            _activeInvokes--;
            if (_draining && _activeInvokes == 0)
                signal = _drainSignal;
        }
        signal?.TrySetResult();
    }

    internal async Task WaitForHealthAsync(CancellationToken ct)
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
        _status = ContainerPluginStatus.Failed;
        throw new TimeoutException(
            $"[{ContainerPluginUrns.StartupTimeout}] Container plugin '{_descriptor.Name}' " +
            $"did not reach Ready within {_descriptor.StartupTimeoutSeconds}s.");
    }

    public async ValueTask DisposeAsync()
    {
        _healthClient.Dispose();
        if (_containerId is not null)
            await StopAsync().ConfigureAwait(false);
        _docker.Dispose();
    }

    private async Task RemoveExistingContainerAsync(string name, CancellationToken ct)
    {
        try
        {
            var containers = await _docker.Containers.ListContainersAsync(
                new ContainersListParameters { All = true, Filters = new Dictionary<string, IDictionary<string, bool>> { { "name", new Dictionary<string, bool> { { name, true } } } } },
                ct).ConfigureAwait(false);
            foreach (var c in containers)
            {
                await _docker.Containers.RemoveContainerAsync(c.ID,
                    new ContainerRemoveParameters { Force = true }, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove existing container '{Name}'", name);
        }
    }
}

internal enum ContainerReplaceOutcome
{
    Success = 0,
    StartFailed = 1,
    HandshakeFailed = 2,
    HandlerTypeNameChanged = 3,
}

internal sealed record ContainerReplaceResult(
    ContainerReplaceOutcome Outcome,
    string? ErrorDetail = null);
