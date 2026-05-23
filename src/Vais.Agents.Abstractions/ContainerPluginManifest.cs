// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Registered container plugin manifest. Describes a containerised agent handler
/// that the runtime discovers via <c>POST /v1/plugins</c> (CLI apply) or filesystem
/// scan at startup (legacy). Mirrors <see cref="McpServerManifest"/> in shape.
/// </summary>
public sealed record ContainerPluginManifest(
    string Id,
    string Version,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Labels = null)
{
    /// <summary>Container image and runtime configuration.</summary>
    public required ContainerPluginSpec Spec { get; init; }
}

/// <summary>Runtime spec for a container plugin.</summary>
public sealed record ContainerPluginSpec
{
    /// <summary>Container image reference (e.g., <c>my-registry/plugin:1.0</c>).</summary>
    public required string Image { get; init; }

    /// <summary>Optional client-side build configuration for build-on-apply. Stored by the server but not acted upon at deploy time.</summary>
    public ContainerPluginBuildSpec? Build { get; init; }

    /// <summary>Port the container exposes for the IP-1 HTTP protocol. Default: 8080.</summary>
    public int Port { get; init; } = 8080;

    /// <summary>Deployment topology: <c>standalone</c> | <c>sidecar</c> | <c>kubernetes</c>. Default: standalone.</summary>
    public string Topology { get; init; } = "standalone";

    /// <summary>Seconds to wait for the container health check on startup. Default: 30.</summary>
    public int StartupTimeoutSeconds { get; init; } = 30;

    /// <summary>Seconds allowed per invoke call before timing out. Default: 60.</summary>
    public int InvokeTimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// Full call-token lifetime in seconds for one invoke/session. When null, defaults to
    /// <see cref="InvokeTimeoutSeconds"/> + 30 (today's coupled behaviour). Set this to support a
    /// single long-lived invoke (e.g. a co-tenant coding session) without inflating the kill-timeout:
    /// in session mode the runtime issues short, renewable tokens whose validity is bound to a live
    /// invoke lease, up to this lifetime.
    /// </summary>
    public int? SessionTtlSeconds { get; init; }

    /// <summary>
    /// Idle/progress timeout in seconds for a streaming invoke: the runtime aborts the invoke if no
    /// streamed activity (a delta or an SSE heartbeat) arrives for this long, reclaiming a wedged
    /// container fast without capping a healthy long session. Applies to the <c>/v1/stream</c> path only
    /// (the non-streaming path has no liveness channel — long-lived plugins should stream). When null,
    /// no idle watchdog runs. Independent of the absolute cap, which is <see cref="SessionTtlSeconds"/>.
    /// </summary>
    public int? InvokeIdleTimeoutSeconds { get; init; }

    /// <summary>Docker image pull policy: <c>Always</c> | <c>IfNotPresent</c> | <c>Never</c>. Default: IfNotPresent.</summary>
    public string ImagePullPolicy { get; init; } = "IfNotPresent";

    /// <summary>Optional retry policy for failed invocations.</summary>
    public ContainerPluginRetryPolicy? RetryPolicy { get; init; }

    /// <summary>Kubernetes-specific configuration (required when <see cref="Topology"/> is <c>kubernetes</c>).</summary>
    public ContainerPluginKubernetesConfig? Kubernetes { get; init; }

    /// <summary>Secret references injected as environment variables. Key = env-var name; value = <c>secret://</c> URI.</summary>
    public IReadOnlyDictionary<string, string>? Secrets { get; init; }

    /// <summary>
    /// Opt-in writable workspace mount. Absent ⇒ today's behaviour (read-only rootfs plus the 64 MB
    /// ephemeral <c>/tmp</c> tmpfs only). When present, the runtime mounts one writable working tree
    /// at <see cref="ContainerPluginWorkspaceSpec.Path"/> for the container's lifetime.
    /// </summary>
    public ContainerPluginWorkspaceSpec? Workspace { get; init; }
}

/// <summary>
/// Declares a single writable workspace mount for a container plugin. The workspace is the only added
/// writable path; the read-only rootfs and all other hardening are preserved. Scoped to the container's
/// lifetime — not per-invoke or per-session, since a plugin container is shared across invocations.
/// </summary>
public sealed record ContainerPluginWorkspaceSpec
{
    /// <summary>Absolute mount path inside the container. May not be <c>/</c> or <c>/tmp</c>. Default <c>/workspace</c>.</summary>
    public string Path { get; init; } = "/workspace";

    /// <summary>Required workspace size in MiB (must be &gt; 0); clamped to the operator maximum. A hard cap for the <c>memory</c> medium; an advisory cap for <c>disk</c>.</summary>
    public required int SizeMb { get; init; }

    /// <summary>
    /// Storage backend identifier. <c>disk</c> (default) = a Docker named volume (K8s: PVC or emptyDir);
    /// <c>memory</c> = a RAM-backed tmpfs (size hard-enforced, counts against the container memory limit).
    /// Modelled as an open string so future centralized backends slot in without a contract change.
    /// </summary>
    public string Medium { get; init; } = "disk";

    /// <summary>
    /// When <c>true</c> the workspace survives container restart/replace (a named volume keyed by plugin
    /// id; K8s: a PVC). When <c>false</c> (default) it is created on start and removed on stop/drain.
    /// <c>true</c> combined with <see cref="Medium"/> <c>memory</c> is invalid — tmpfs cannot persist.
    /// </summary>
    public bool Persist { get; init; }
}

/// <summary>Build specification for client-side build-on-apply. Ignored by the server at deploy time.</summary>
public sealed record ContainerPluginBuildSpec
{
    /// <summary>Docker build context path. Relative paths are resolved against the manifest file's directory.</summary>
    public required string Context { get; init; }

    /// <summary>Dockerfile path relative to <see cref="Context"/>. Default: Dockerfile.</summary>
    public string Dockerfile { get; init; } = "Dockerfile";

    /// <summary>Optional <c>--build-arg</c> key=value pairs.</summary>
    public IReadOnlyDictionary<string, string>? Args { get; init; }

    /// <summary>Run <c>docker push</c> after a successful build. Default: false.</summary>
    public bool Push { get; init; }
}

/// <summary>Retry policy for failed plugin invocations.</summary>
public sealed record ContainerPluginRetryPolicy(
    int MaxAttempts,
    int BackoffSeconds,
    IReadOnlyList<string> RetryOn);

/// <summary>Kubernetes deployment coordinates for the <c>kubernetes</c> topology.</summary>
public sealed record ContainerPluginKubernetesConfig
{
    /// <summary>URL of the Kubernetes Service the runtime should probe (e.g., <c>http://my-plugin.default.svc.cluster.local:8080</c>).</summary>
    public required string ServiceUrl { get; init; }

    /// <summary>Kubernetes Deployment name (used for image patching).</summary>
    public string DeploymentName { get; init; } = "";

    /// <summary>Kubernetes namespace. Default: default.</summary>
    public string Namespace { get; init; } = "default";
}

/// <summary>Stable identity reference to a registered <see cref="ContainerPluginManifest"/>.</summary>
public sealed record ContainerPluginHandle(string Id, string Version);

/// <summary>Runtime status snapshot returned by <c>IContainerPluginLifecycleManager.QueryAsync</c>.</summary>
public sealed record ContainerPluginRuntimeStatus(
    ContainerPluginHandle Handle,
    string Topology,
    DateTimeOffset RegisteredAt);
