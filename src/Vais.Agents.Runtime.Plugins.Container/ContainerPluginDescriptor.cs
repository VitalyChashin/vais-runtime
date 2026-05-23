// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Descriptor for one container plugin, produced by <see cref="ContainerPluginHostService"/>
/// from a <c>plugin.yaml</c> with <c>runtime: container</c>. Fields populated at parse time
/// are immutable; <see cref="HandlerTypeName"/> and <see cref="TargetApiVersion"/> are filled
/// after the startup metadata handshake.
/// </summary>
internal sealed class ContainerPluginDescriptor
{
    public required string Name { get; init; }
    public required string Image { get; set; }
    public int Port { get; init; } = 8080;
    public string HandlerTypeName { get; set; } = "";
    public string TargetApiVersion { get; set; } = "";
    public ContainerTopology Topology { get; init; } = ContainerTopology.Standalone;
    public int StartupTimeoutSeconds { get; init; } = 30;
    public int InvokeTimeoutSeconds { get; init; } = 60;
    public int? SessionTtlSeconds { get; init; }
    public int? InvokeIdleTimeoutSeconds { get; init; }
    public ContainerRetryPolicy? RetryPolicy { get; init; }
    public IReadOnlyDictionary<string, string> SecretRefs { get; init; } =
        new Dictionary<string, string>();
    public string InvokeBaseUrl { get; init; } = "";
    public KubernetesPluginConfig? KubernetesConfig { get; init; }
    public long? MemoryBytes { get; init; }
    public long? NanoCpus    { get; init; }
    public long? PidsLimit   { get; init; }
    /// <summary>
    /// Docker network name for internal-network mode (Phase 2 isolation).
    /// Null = legacy host-runtime mode (plugin port published to 127.0.0.1).
    /// </summary>
    public string? DockerPluginNetwork { get; init; }

    /// <summary>
    /// Validated writable-workspace configuration parsed from <c>spec.workspace</c>, or null when none
    /// is declared (today's behaviour: read-only rootfs + 64 MB <c>/tmp</c> only).
    /// </summary>
    public ContainerWorkspaceConfig? Workspace { get; init; }
}

internal enum ContainerTopology { Sidecar, Standalone, Kubernetes }

/// <summary>
/// Storage backend for a plugin workspace. Open set — a future centralized backend (e.g. s3-snapshot)
/// is added here plus its branch in <see cref="DockerContainerSupervisor.BuildHostConfig"/>.
/// </summary>
internal enum WorkspaceMedium { Disk, Memory }

/// <summary>Validated workspace configuration carried on a <see cref="ContainerPluginDescriptor"/>.</summary>
internal sealed record ContainerWorkspaceConfig(string Path, int SizeMb, WorkspaceMedium Medium, bool Persist);

internal sealed record ContainerRetryPolicy(
    int MaxAttempts,
    int BackoffSeconds,
    IReadOnlyList<string> RetryOn);
