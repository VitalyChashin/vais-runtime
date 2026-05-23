// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Manages the Docker container lifecycle for one container plugin.
/// States: Created → Starting → Ready → Stopping → Stopped / Failed.
/// </summary>
internal sealed class DockerContainerSupervisor : IContainerSupervisor
{
    private readonly ContainerPluginDescriptor _descriptor;
    private readonly IDockerClient _docker;
    private readonly HttpClient _healthClient;
    private readonly ILogger _logger;
    private readonly ICallTokenService? _callTokenService;
    private readonly string? _otlpEndpointUrl;
    private readonly string? _logEndpointUrl;

    private ContainerPluginStatus _status = ContainerPluginStatus.Created;
    private string? _containerId;

    private int _activeInvokes;
    private bool _draining;
    private TaskCompletionSource? _drainSignal;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _replaceLock = new(1, 1);

    private const long DefaultMemoryBytes = 256L * 1024 * 1024;
    private const long DefaultNanoCpus    = 500_000_000L;
    private const long DefaultPidsLimit   = 128;

    public ContainerPluginDescriptor Descriptor => _descriptor;
    public ContainerPluginStatus Status => _status;

    internal DockerContainerSupervisor(
        ContainerPluginDescriptor descriptor,
        IDockerClient docker,
        ILogger logger,
        ICallTokenService? callTokenService = null,
        string? otlpEndpointUrl = null,
        string? logEndpointUrl = null)
    {
        _descriptor = descriptor;
        _docker = docker;
        _healthClient = new HttpClient { BaseAddress = new Uri(descriptor.InvokeBaseUrl) };
        _logger = logger;
        _callTokenService = callTokenService;
        _otlpEndpointUrl = otlpEndpointUrl;
        _logEndpointUrl = logEndpointUrl;
    }

    public async Task StartAsync(CancellationToken ct = default)
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

        if (_otlpEndpointUrl is not null && _callTokenService is not null)
        {
            // Token uses the plugin name in both slots — the OTLP receiver validates via TryExtract
            // (it doesn't need to match a specific runId/agentId pair, just be a valid signed token).
            var otlpToken = _callTokenService.Generate(
                runId: _descriptor.Name, agentId: _descriptor.Name, ttlSeconds: 86_400);
            envVars.Add($"OTEL_EXPORTER_OTLP_ENDPOINT={_otlpEndpointUrl}");
            envVars.Add("OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf");
            envVars.Add($"OTEL_EXPORTER_OTLP_HEADERS=Authorization=vais-plugin-token {otlpToken}");
            envVars.Add($"OTEL_RESOURCE_ATTRIBUTES=vais.agent_id={_descriptor.Name}");
        }

        if (_logEndpointUrl is not null && _callTokenService is not null)
        {
            var logToken = _callTokenService.Generate(
                runId: _descriptor.Name, agentId: _descriptor.Name, ttlSeconds: 86_400);
            // Include the source discriminator in the URL; the Python SDK posts directly to this URL.
            var fullLogUrl = $"{_logEndpointUrl}?source=plugin&id={Uri.EscapeDataString(_descriptor.Name)}";
            envVars.Add($"VAIS_LOG_ENDPOINT={fullLogUrl}");
            envVars.Add($"VAIS_LOG_TOKEN={logToken}");
        }

        var exposedPorts = new Dictionary<string, EmptyStruct>
        {
            { $"{_descriptor.Port}/tcp", new EmptyStruct() }
        };

        var containerName = DockerNaming.ContainerName(_descriptor.Name);
        await RemoveExistingContainerAsync(containerName, ct).ConfigureAwait(false);
        await EnsureWorkspaceVolumeAsync(ct).ConfigureAwait(false);

        var createResp = await _docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Name = containerName,
                Image = _descriptor.Image,
                Env = envVars,
                ExposedPorts = exposedPorts,
                HostConfig = BuildHostConfig(_descriptor),
            }, ct).ConfigureAwait(false);

        _containerId = createResp.ID;
        await _docker.Containers.StartContainerAsync(_containerId, null, ct).ConfigureAwait(false);

        await WaitForHealthAsync(ct).ConfigureAwait(false);
        _status = ContainerPluginStatus.Ready;
        _logger.LogInformation(
            "Container plugin '{Name}' is ready (containerId={ContainerId})", _descriptor.Name, _containerId);
    }

    public async Task StopAsync(CancellationToken ct = default)
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

        // Ephemeral disk workspaces are reclaimed with the container; persistent ones survive
        // (DrainAndReplace = Stop+Start, so ephemeral resets on replace, persistent is kept).
        if (_descriptor.Workspace is { Medium: WorkspaceMedium.Disk, Persist: false })
            await RemoveWorkspaceVolumeAsync(DockerNaming.WorkspaceVolumeName(_descriptor.Name)).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the disk-medium workspace volume before container start. Idempotent for persistent
    /// volumes (reused across restarts); ephemeral volumes are removed first so each start is clean.
    /// Memory-medium workspaces are tmpfs and need no volume.
    /// </summary>
    internal async Task EnsureWorkspaceVolumeAsync(CancellationToken ct)
    {
        if (_descriptor.Workspace is not { Medium: WorkspaceMedium.Disk } ws) return;
        var volumeName = DockerNaming.WorkspaceVolumeName(_descriptor.Name);
        if (!ws.Persist)
            await RemoveWorkspaceVolumeAsync(volumeName).ConfigureAwait(false);
        await _docker.Volumes.CreateAsync(
            new VolumesCreateParameters
            {
                Name = volumeName,
                Labels = new Dictionary<string, string>
                {
                    ["vais.plugin"] = _descriptor.Name,
                    ["vais.workspace"] = "true",
                },
            }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a persistent disk workspace volume on explicit plugin removal (not on stop/restart).
    /// No-op for ephemeral, memory, or no-workspace plugins. Called by the host on unregister.
    /// </summary>
    internal async Task RemovePersistentWorkspaceAsync()
    {
        if (_descriptor.Workspace is { Medium: WorkspaceMedium.Disk, Persist: true })
            await RemoveWorkspaceVolumeAsync(DockerNaming.WorkspaceVolumeName(_descriptor.Name)).ConfigureAwait(false);
    }

    private async Task RemoveWorkspaceVolumeAsync(string volumeName)
    {
        try
        {
            await _docker.Volumes.RemoveAsync(volumeName, force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove workspace volume '{Volume}'", volumeName);
        }
    }

    public async Task<ContainerReplaceResult> DrainAndReplaceAsync(string? newImage, CancellationToken ct)
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

    public bool TryAcquireInvoke()
    {
        lock (_stateLock)
        {
            if (_draining || _status != ContainerPluginStatus.Ready) return false;
            _activeInvokes++;
            return true;
        }
    }

    public void ReleaseInvoke()
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

    internal static HostConfig BuildHostConfig(ContainerPluginDescriptor descriptor)
    {
        var hostConfig = new HostConfig
        {
            ReadonlyRootfs = true,
            Tmpfs          = new Dictionary<string, string> { { "/tmp", "rw,size=64m,mode=1777" } },
            CapDrop        = new List<string> { "ALL" },
            SecurityOpt    = new List<string> { "no-new-privileges:true" },
            Memory         = descriptor.MemoryBytes ?? DefaultMemoryBytes,
            MemorySwap     = descriptor.MemoryBytes ?? DefaultMemoryBytes,
            NanoCPUs       = descriptor.NanoCpus    ?? DefaultNanoCpus,
            PidsLimit      = descriptor.PidsLimit   ?? DefaultPidsLimit,
        };

        // Opt-in writable workspace: the only added writable path; rootfs stays read-only.
        if (descriptor.Workspace is { } ws)
        {
            if (ws.Medium == WorkspaceMedium.Memory)
            {
                // RAM-backed tmpfs: size is a hard kernel cap (counts against the memory limit).
                hostConfig.Tmpfs[ws.Path] = $"rw,size={ws.SizeMb}m,mode=1777";
            }
            else
            {
                // Disk-backed named volume (created/removed in StartAsync/StopAsync); size is advisory.
                hostConfig.Mounts = new List<Mount>
                {
                    new()
                    {
                        Type = "volume",
                        Source = DockerNaming.WorkspaceVolumeName(descriptor.Name),
                        Target = ws.Path,
                    },
                };
            }
        }

        if (descriptor.DockerPluginNetwork is { Length: > 0 } networkName)
        {
            // Internal-network mode: plugin is on a shared Docker network with the runtime.
            // No host port published; the runtime reaches the plugin via container-DNS.
            hostConfig.NetworkMode = networkName;
        }
        else
        {
            // Legacy host-runtime mode: publish the plugin port to 127.0.0.1 only.
            hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                { $"{descriptor.Port}/tcp",
                  new List<PortBinding> { new() { HostIP = "127.0.0.1", HostPort = descriptor.Port.ToString() } } }
            };
        }

        return hostConfig;
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
    RolloutStarted = 4,
}

internal sealed record ContainerReplaceResult(
    ContainerReplaceOutcome Outcome,
    string? ErrorDetail = null);
